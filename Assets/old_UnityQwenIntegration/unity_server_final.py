import os
import uuid
import time
import gc
import copy
import signal
import threading
from typing import Dict, List, Set, Any, Optional, Tuple
from fastapi import FastAPI, File, Form, UploadFile, BackgroundTasks, Response
from transformers import AutoProcessor, Qwen2VLForConditionalGeneration
from qwen_vl_utils import process_vision_info
import torch

# Configure PyTorch for better memory management
if torch.cuda.is_available():
    # Set PyTorch to use deterministic algorithms for better memory usage
    torch.backends.cudnn.deterministic = True
    # Disable benchmarking to reduce memory spikes
    torch.backends.cudnn.benchmark = False
    # Try to enable expandable segments for better memory allocation
    os.environ["PYTORCH_CUDA_ALLOC_CONF"] = "expandable_segments:True"

# Model path for the fine-tuned checkpoint
MODEL_PATH = "LLaMA-Factory/saves/qwen2-vl4/checkpoint-36"

# Initialize FastAPI
app = FastAPI()

# Global variables for model and processor
model = None
processor = None

# Request queue control
MAX_CONCURRENT_REQUESTS = 1  # Only allow one request at a time
request_semaphore = threading.Semaphore(MAX_CONCURRENT_REQUESTS)

# Counter for requests to trigger model reloading
request_counter = 0
RELOAD_MODEL_EVERY = 5  # Reload model more frequently to prevent memory buildup

# Global settings - extremely aggressive history limits
MAX_HISTORY_PAIRS = 4  # Just 2 pairs + system message = 5 total messages
MAX_TOTAL_MESSAGES = (MAX_HISTORY_PAIRS * 2) + 1

# Memory pressure tracking
memory_pressure = False
last_reload_time = 0

# Watchdog timer to detect hangs
watchdog_timer = None
WATCHDOG_TIMEOUT = 60  # seconds

# Memory usage thresholds
MEMORY_WARNING_THRESHOLD = 0.7  # 70% of GPU memory used
MEMORY_CRITICAL_THRESHOLD = 0.85  # 85% of GPU memory used

# Model parameters
MODEL_DTYPE = torch.float16 if torch.cuda.is_available() else torch.float32

# Function to load the model with specific memory optimizations
def load_model():
    global model, processor, last_reload_time
    
    try:
        # Clear memory first
        unload_model()
        
        print(f"Loading Qwen2-VL model from {MODEL_PATH}...")
        
        # Load with aggressive memory optimization settings
        model = Qwen2VLForConditionalGeneration.from_pretrained(
            MODEL_PATH,
            torch_dtype=MODEL_DTYPE,
            device_map="auto",
            # Add low_cpu_mem_usage for more efficient loading
            low_cpu_mem_usage=True,
            # Use 8-bit quantization for smaller memory footprint
            load_in_8bit=False,  # Enable if you have bitsandbytes installed
        )
        processor = AutoProcessor.from_pretrained(MODEL_PATH)
        
        # Update last reload time
        last_reload_time = time.time()
        
        print("Model and processor loaded successfully.")
        
        # Print memory usage
        if torch.cuda.is_available():
            allocated = torch.cuda.memory_allocated() / 1024**3
            reserved = torch.cuda.memory_reserved() / 1024**3
            print(f"GPU memory allocated: {allocated:.2f} GiB")
            print(f"GPU memory reserved: {reserved:.2f} GiB")
            
        return True
        
    except Exception as e:
        print(f"Error loading model: {e}")
        return False

def unload_model():
    """Explicitly unload the model and free all resources"""
    global model, processor
    
    print("Unloading model...")
    
    # Delete model and processor
    if model is not None:
        try:
            del model
            model = None
        except:
            pass
    
    if processor is not None:
        try:
            del processor
            processor = None
        except:
            pass
    
    # Force garbage collection multiple times
    for _ in range(3):
        gc.collect()
    
    # Clear CUDA cache
    if torch.cuda.is_available():
        try:
            torch.cuda.empty_cache()
            torch.cuda.synchronize()  # Make sure operations are complete
            
            # Print memory status after unloading
            allocated = torch.cuda.memory_allocated() / 1024**3
            reserved = torch.cuda.memory_reserved() / 1024**3
            print(f"After unloading - GPU memory allocated: {allocated:.2f} GiB, reserved: {reserved:.2f} GiB")
        except:
            pass
    
    return True

# Function to start watchdog
def start_watchdog():
    """Start a watchdog timer to detect and recover from hangs"""
    global watchdog_timer
    
    # Cancel any existing watchdog
    if watchdog_timer:
        watchdog_timer.cancel()
    
    # Define watchdog function
    def watchdog_function():
        print(f"WATCHDOG TRIGGERED after {WATCHDOG_TIMEOUT} seconds - server may be hung")
        # Force model reload
        unload_model()
        # Try to free any remaining memory
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
        # Reset request counter
        global request_counter
        request_counter = 0
    
    # Start watchdog timer
    watchdog_timer = threading.Timer(WATCHDOG_TIMEOUT, watchdog_function)
    watchdog_timer.daemon = True
    watchdog_timer.start()

# Function to stop watchdog
def stop_watchdog():
    """Stop the watchdog timer"""
    global watchdog_timer
    if watchdog_timer:
        watchdog_timer.cancel()
        watchdog_timer = None

# Simple conversation storage
conversations: Dict[str, Dict[str, Any]] = {}

# Session cleanup parameters
SESSION_TIMEOUT = 3600  # 1 hour in seconds
CLEANUP_INTERVAL = 300  # Run cleanup every 5 minutes

# System message for the assistant
SYSTEM_MESSAGE = """You are an AI task assistant for humans in VR environments. 
Act positively and be supportive while remaining concise. Your answers should generally be one sentence long.

Your main responsibilities:
1. Answer questions about the VR environment when asked
2. Provide guidance on the next steps of tasks when requested
3. Maintain conversation context to avoid repeating advice
4. Help locate objects when asked

If asked about object location, describe where to find it in the environment.
When advising on task steps, suggest only one action at a time.
Remember previous steps discussed in the conversation to ensure your advice is progressive.

Be specific, clear, and helpful while using natural, conversational language."""

# Function to get a conversation by ID or create a new one
def get_conversation(session_id: Optional[str] = None) -> Tuple[str, Dict[str, Any]]:
    # Generate new session ID if not provided
    if not session_id:
        session_id = str(uuid.uuid4())
        print(f"Created new session ID: {session_id}")
    else:
        print(f"Using existing session ID: {session_id}")
    
    # Create new conversation if it doesn't exist
    if session_id not in conversations:
        conversations[session_id] = {
            "messages": [
                {
                    "role": "system",
                    "content": SYSTEM_MESSAGE,
                }
            ],
            "last_updated": time.time(),
            "images": set()  # Track images to clean up
        }
        print(f"Created new conversation for session {session_id}")
    else:
        # Update timestamp
        conversations[session_id]["last_updated"] = time.time()
        print(f"Updated timestamp for session {session_id}")
    
    return session_id, conversations[session_id]

# Ultra-strict conversation history management
def enforce_history_limit(conversation: Dict[str, Any], session_id: str) -> Dict[str, Any]:
    """
    Extremely strict enforcement of conversation history length
    """
    messages = conversation["messages"]
    total_messages = len(messages)
    
    # If over limit, remove oldest pairs
    if total_messages > MAX_TOTAL_MESSAGES:
        # Calculate how many messages to remove (must be even number for complete pairs)
        excess_messages = total_messages - MAX_TOTAL_MESSAGES
        # If odd, add 1 to ensure we remove complete pairs
        if excess_messages % 2 == 1:
            excess_messages += 1
            
        # Images to remove
        images_to_remove = set()
        
        # Identify images to remove (from user messages)
        for i in range(1, 1 + excess_messages, 2):  # Start after system, step by 2 for user messages
            if i >= len(messages):
                break
                
            user_msg = messages[i]
            if isinstance(user_msg["content"], list):
                for content_item in user_msg["content"]:
                    if content_item.get("type") == "image" and "image" in content_item:
                        image_path = content_item["image"]
                        images_to_remove.add(image_path)
                        print(f"Marking image for removal: {image_path}")
        
        # Remove oldest pairs while keeping system message
        messages = [messages[0]] + messages[1+excess_messages:]
        conversation["messages"] = messages
        
        # Actually remove the images
        for image_path in images_to_remove:
            try:
                if os.path.exists(image_path):
                    os.remove(image_path)
                    if image_path in conversation["images"]:
                        conversation["images"].remove(image_path)
                    print(f"Removed old image: {image_path}")
            except Exception as e:
                print(f"Error removing image {image_path}: {e}")
        
        # Verify the result (for debugging)
        print(f"After strict pruning: session {session_id} has {len(messages)} messages")
            
    return conversation

# Function to clean up expired sessions
def cleanup_sessions() -> None:
    current_time = time.time()
    expired_sessions = []
    
    for session_id, data in conversations.items():
        if current_time - data["last_updated"] > SESSION_TIMEOUT:
            expired_sessions.append(session_id)
    
    for session_id in expired_sessions:
        # Clean up images before removing session
        for image_path in conversations[session_id].get("images", set()):
            try:
                if os.path.exists(image_path):
                    os.remove(image_path)
            except Exception as e:
                print(f"Error removing image {image_path}: {e}")
        
        # Remove the session
        del conversations[session_id]
    
    if expired_sessions:
        print(f"Cleaned up {len(expired_sessions)} expired sessions")

# Aggressive memory management functions
def free_gpu_memory() -> None:
    """Force aggressive cleanup of GPU memory"""
    # Force garbage collection
    gc.collect()
    
    # Clear CUDA cache
    if torch.cuda.is_available():
        try:
            torch.cuda.empty_cache()
            torch.cuda.synchronize()
            
            allocated = torch.cuda.memory_allocated() / 1024**3
            reserved = torch.cuda.memory_reserved() / 1024**3
            print(f"Cleared CUDA cache. Memory stats - Allocated: {allocated:.2f} GiB, Reserved: {reserved:.2f} GiB")
        except Exception as e:
            print(f"Error clearing CUDA cache: {e}")

def check_memory_usage() -> float:
    """Check memory usage and return ratio of used memory"""
    if not torch.cuda.is_available():
        return 0.0
    
    try:
        # Get memory statistics
        total_memory = torch.cuda.get_device_properties(0).total_memory
        reserved_memory = torch.cuda.memory_reserved()
        
        # Calculate the ratio of reserved to total memory
        memory_usage_ratio = reserved_memory / total_memory
        
        # Log memory status
        memory_status = f"Memory usage: {memory_usage_ratio:.2%}"
        if memory_usage_ratio > MEMORY_CRITICAL_THRESHOLD:
            print(f"CRITICAL MEMORY PRESSURE! {memory_status}")
        elif memory_usage_ratio > MEMORY_WARNING_THRESHOLD:
            print(f"WARNING: High memory usage. {memory_status}")
        
        return memory_usage_ratio
    except:
        return 0.0

def handle_memory_pressure() -> None:
    """Handle memory pressure based on current usage"""
    ratio = check_memory_usage()
    
    if ratio > MEMORY_CRITICAL_THRESHOLD:
        print("Critical memory pressure detected. Performing emergency reload.")
        # Complete model reload
        unload_model()
        time.sleep(1)  # Give system some time
        load_model()
        return True
    elif ratio > MEMORY_WARNING_THRESHOLD:
        print("High memory pressure detected. Performing cleanup.")
        # Move model to CPU temporarily if possible
        if model is not None and torch.cuda.is_available():
            try:
                model.to("cpu")
                free_gpu_memory()
                model.to("cuda")
                print("Moved model to CPU and back to free memory")
            except:
                print("Failed to move model between devices")
        else:
            free_gpu_memory()
        return True
    
    return False

def perform_inference(conversation: Dict[str, Any], session_id: str) -> str:
    """Perform model inference with memory management and safeguards"""
    global request_counter, model, processor
    
    # Start watchdog timer
    start_watchdog()
    
    try:
        # Check if we need to reload the model
        reload_needed = False
        
        # Increment request counter
        request_counter += 1
        
        # Check if we need to reload the model based on request count
        if request_counter >= RELOAD_MODEL_EVERY:
            print(f"Request count ({request_counter}) reached reload threshold. Reloading model...")
            reload_needed = True
        
        # Check if it's been more than 10 minutes since last reload
        if time.time() - last_reload_time > 600:  # 10 minutes
            print(f"Time since last reload ({time.time() - last_reload_time:.0f}s) exceeds threshold. Reloading model...")
            reload_needed = True
        
        # Check memory pressure
        if handle_memory_pressure():
            reload_needed = True
        
        # Reload model if needed
        if reload_needed or model is None or processor is None:
            unload_model()
            load_model()
            request_counter = 0
        
        # Make a copy of the conversation to avoid memory issues
        messages_copy = copy.deepcopy(conversation["messages"])
        
        # Print conversation for debugging
        print(f"Processing conversation with {len(messages_copy)} messages")
        
        # Prepare inputs for the model
        prompt = processor.apply_chat_template(
            messages_copy, tokenize=False, add_generation_prompt=True
        )
        
        # Process images - this is memory intensive
        image_inputs, _ = process_vision_info(messages_copy)
        
        # Create model inputs
        inputs = processor(
            text=[prompt],
            images=image_inputs,
            padding=True,
            return_tensors="pt"
        ).to(model.device)
        
        # Generate output with reduced parameters to save memory
        outputs = model.generate(
            **inputs, 
            max_new_tokens=80,  # Reduced from 256/100 to save memory
            temperature=0.7,
            top_p=0.9,
            repetition_penalty=1.1
        )
        
        # Extract text
        generated_text = processor.tokenizer.decode(
            outputs[0], skip_special_tokens=True
        ).strip()
        
        # Extract the assistant's response
        if "assistant" in generated_text:
            generated_text = generated_text.split("\nassistant", 1)[-1].strip()
        
        # Clean up tensors to free memory
        del outputs
        del inputs
        del image_inputs
        
        # Stop watchdog
        stop_watchdog()
        
        # Force garbage collection
        free_gpu_memory()
        
        return generated_text
        
    except Exception as e:
        print(f"Error during inference: {e}")
        # Stop watchdog
        stop_watchdog()
        # Force cleanup on error
        free_gpu_memory()
        # Reload model on error
        unload_model()
        load_model()
        # Return error message
        return f"I'm sorry, I couldn't process that request. Please try again."

# Schedule cleanup
@app.on_event("startup")
async def startup_event():
    # Load model
    load_model()
    
    # Initial cleanup
    cleanup_sessions()

# Cleanup on shutdown
@app.on_event("shutdown")
async def shutdown_event():
    # Clean up model resources
    unload_model()

@app.post("/predict_simple")
async def predict_simple(
    text: str = Form(...),
    image: UploadFile = File(...),
    session_id: Optional[str] = Form(None),
    task_context: str = Form("Task: collect all sweets into the metal bowl. "),
    background_tasks: BackgroundTasks = None
):
    """
    A simpler implementation where the server maintains conversation history.
    The client only needs to keep track of session_id between requests.
    Includes queue management to prevent concurrent requests.
    """
    # Only allow one request at a time to prevent memory issues
    if not request_semaphore.acquire(blocking=False):
        return Response(
            content="Server is busy processing another request. Please try again.",
            media_type="text/plain",
            status_code=503  # Service Unavailable
        )
    
    try:
        # Free memory before processing
        free_gpu_memory()
        
        # Get or create conversation
        session_id, conversation = get_conversation(session_id)
        
        # Save uploaded image temporarily
        image_filename = f"temp_simple_{session_id}_{int(time.time())}.png"
        with open(image_filename, "wb") as f:
            f.write(await image.read())
        
        # Track the image
        conversation["images"].add(image_filename)
        
        # Add user message to conversation with task context for the first message only
        if len(conversation["messages"]) == 1:  # Only system message exists
            full_text = task_context + text
            print(f"New conversation started with task context: {task_context}")
        else:
            full_text = text  # No task context for follow-up messages
        
        conversation["messages"].append({
            "role": "user",
            "content": [
                {"type": "image", "image": image_filename},
                {"type": "text", "text": full_text},
            ],
        })
        
        # STRICT limit enforcement before inference (CRITICAL)
        enforce_history_limit(conversation, session_id)
        
        # Log conversation length for debugging
        msg_count = len(conversation["messages"])
        print(f"Conversation length before inference: {msg_count} messages (max allowed: {MAX_TOTAL_MESSAGES})")
        
        # Run inference with all safety measures
        generated_text = perform_inference(conversation, session_id)
        
        # Add assistant response to conversation
        conversation["messages"].append({
            "role": "assistant",
            "content": generated_text
        })
        
        # Log conversation length after adding assistant message
        msg_count = len(conversation["messages"])
        print(f"Conversation length after adding response: {msg_count} messages")
        
        # Enforce limit again to be absolutely sure we're under the limit
        enforce_history_limit(conversation, session_id)
        
        # Run session cleanup if needed
        if time.time() % CLEANUP_INTERVAL < 1:
            cleanup_sessions()
        
        # Schedule background cleanup
        if background_tasks is not None:
            background_tasks.add_task(free_gpu_memory)
        
        # Create a response with session ID in the header
        response = Response(content=generated_text, media_type="text/plain")
        response.headers["X-Session-ID"] = session_id
        
        print(f"Returning response with session ID header: {session_id}")
        return response
    
    except Exception as e:
        print(f"Error during processing: {e}")
        return Response(
            content=f"Error: {str(e)}",
            media_type="text/plain",
            status_code=500
        )
    finally:
        # Always release the semaphore
        request_semaphore.release()

# Get conversation history for debugging
@app.get("/conversation/{session_id}")
async def get_conversation_history(session_id: str):
    """Get the conversation history for a given session ID."""
    if session_id in conversations:
        # For privacy/security, don't return actual image paths
        messages = []
        for msg in conversations[session_id]["messages"]:
            if msg["role"] == "user" and isinstance(msg["content"], list):
                # Replace image paths with placeholders for display
                content_copy = []
                for item in msg["content"]:
                    if item.get("type") == "image":
                        content_copy.append({"type": "image", "image": "<image>"})
                    else:
                        content_copy.append(item)
                messages.append({"role": msg["role"], "content": content_copy})
            else:
                messages.append(msg)
        
        return {
            "session_id": session_id, 
            "messages": messages,
            "message_count": len(conversations[session_id]["messages"]),
            "history_limit": MAX_HISTORY_PAIRS,
            "max_total_messages": MAX_TOTAL_MESSAGES
        }
    else:
        return {"error": "Session not found"}

# Memory management endpoint
@app.post("/cleanup_memory")
async def cleanup_memory():
    """Force memory cleanup on the server"""
    try:
        # Clean up old sessions
        cleanup_sessions()
        
        # Force unload and reload model
        unload_model()
        load_model()
        
        # Reset request counter
        global request_counter
        request_counter = 0
        
        return {"status": "success", "memory_cleared": True}
    except Exception as e:
        return {"status": "error", "message": str(e)}

@app.get("/")
def read_root():
    return {"message": "Qwen2-VL Server is running with extreme memory management", "max_history_pairs": MAX_HISTORY_PAIRS}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8080)