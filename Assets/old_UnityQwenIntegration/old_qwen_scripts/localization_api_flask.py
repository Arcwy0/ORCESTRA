from flask import Flask, request, jsonify
from modelscope import (
   snapshot_download,
   AutoModelForCausalLM,
   AutoTokenizer,
   GenerationConfig,
)
import torch
import base64
import re
from PIL import Image
from io import BytesIO

from peft import AutoPeftModelForCausalLM

DEFAULT_CKPT_PATH = '/app/weights/Qwen-VL-Chat-Int4'
DEFAULT_ADAPT_CKPT_PATH = '/app/output_qwen_localization'
BOX_TAG_PATTERN = r"<box>([\s\S]*?)</box>"
PUNCTUATION = "！？。＂＃＄％＆＇（）＊＋，－／：；＜＝＞＠［＼］＾＿｀｛｜｝～｟｠｢｣､、〃》「」『』【】〔〕〖〗〘〙〚〛〜〝〞〟〰〾〿–—‘’‛“”„‟…‧﹏."

app = Flask(__name__)

torch.manual_seed(1234)

# Load tokenizer, model and generation config
tokenizer = AutoTokenizer.from_pretrained(DEFAULT_CKPT_PATH, trust_remote_code=True)
model = AutoPeftModelForCausalLM.from_pretrained(
    DEFAULT_ADAPT_CKPT_PATH,
    device_map="auto",
    trust_remote_code=True,
).eval()
model.generation_config = GenerationConfig.from_pretrained(DEFAULT_CKPT_PATH, trust_remote_code=True)

# tokenizer = AutoTokenizer.from_pretrained(DEFAULT_CKPT_PATH, trust_remote_code=True)
# model = AutoModelForCausalLM.from_pretrained(
#     DEFAULT_CKPT_PATH,
#     device_map="auto",
#     trust_remote_code=True,
# ).eval()
# model.generation_config = GenerationConfig.from_pretrained(DEFAULT_CKPT_PATH, trust_remote_code=True)

# Initialize Chat History
history = None

# # Start a Flask application that listens for requests from any IP address and port

#basic chat function
@app.route('/chat', methods=['POST'])
def chat():
   global history
   data = request.json
   image_context = data.get('image_context', {})
   text_input = data.get('text', '')
   query = tokenizer.from_list_format([
       image_context,
       {'text': text_input},
   ])
   response, history = model.chat(tokenizer, query=query, history=history)
   return jsonify({'response': response})


#basic bbox_drawing
@app.route('/chat_bbox', methods=['POST'])
def chat_bbox():
   global history
   history = None
   text_input = request.json.get('text', '')
   image_base64 = request.json.get('image_context', {}).get('image', '')
   image_data = base64.b64decode(image_base64)
   image = Image.open(BytesIO(image_data))
   image_path = '/app/input_chat.jpg'
   image.save(image_path)
   query = tokenizer.from_list_format([
       {'image' : image_path},
       {'text': text_input},
   ])
   response, history = model.chat(tokenizer, query=query, history=history)
   image = tokenizer.draw_bbox_on_latest_picture(response, history)
   print(image)
   image_path = '/app/output_chat.jpg'
   image.save(image_path)
   print(image_path)
   history = None
   return jsonify({'response': response, 'image_url': image_path})

@app.route('/describe_view', methods=['POST'])
def describe_view():
   global history
   history = None
   text_input = request.json.get('text', '')
   image_base64 = request.json.get('image_context', {}).get('image', '')
   image_data = base64.b64decode(image_base64)
   image = Image.open(BytesIO(image_data))
   image_path = '/app/describe_view_input.jpg'
   image.save(image_path)
   query = tokenizer.from_list_format([
       {'image' : image_path},
       {'text': text_input},
   ])
   response, history = model.chat(tokenizer, query=query, history=history)
   history = None
   return jsonify({'response': response, 'image_url': image_path})

@app.route('/question_view', methods=['POST'])
def question_view():
   global history
   history = None
   text_input = request.json.get('text', '')
   image_base64 = request.json.get('image_context', {}).get('image', '')
   image_data = base64.b64decode(image_base64)
   image = Image.open(BytesIO(image_data))
   image_path = '/app/question_view_input.jpg'
   image.save(image_path)
   query = tokenizer.from_list_format([
       {'image' : image_path},
       {'text': text_input},
   ])
   response, history = model.chat(tokenizer, query=query, history=history)
   history = None
   return jsonify({'response': response, 'image_url': image_path})
   
   
@app.route('/search_view', methods=['POST'])
def search_view():
   global history
   history = None
   text_input = request.json.get('text', '')
   image_base64 = request.json.get('image_context', {}).get('image', '')
   image_data = base64.b64decode(image_base64)
   image = Image.open(BytesIO(image_data))
   image_path = '/app/search_view_input.jpg'
   image.save(image_path)
   
      # Denormalize the bounding box coordinates
   def denormalize_bbox(bbox):
      x0, y0 = map(int, bbox[0].strip('()').split(','))
      x1, y1 = map(int, bbox[1].strip('()').split(','))
      width, height = image.size
      denormalized_bbox = (
         int(x0 * width / 1000),
         int(y0 * height / 1000),
         int(x1 * width / 1000),
         int(y1 * height / 1000)
      )
      return denormalized_bbox

   query = tokenizer.from_list_format([
      {'image': image_path},
      {'text': text_input},
   ])
   response, history = model.chat(tokenizer, query=query, history=history)

   # Denormalize the bounding box coordinates in the response
   response_parts = response.split('<box>')
   for i in range(1, len(response_parts)):
      bbox_part, rest_part = response_parts[i].split('</box>', 1)
      bbox_coords = bbox_part.split('),(')
      denormalized_bbox_coords = denormalize_bbox(bbox_coords)
      response_parts[i] = f'<box>({denormalized_bbox_coords[0]},{denormalized_bbox_coords[1]}),({denormalized_bbox_coords[2]},{denormalized_bbox_coords[3]})</box>' + rest_part
   response_denormalized = ''.join(response_parts)

   image = tokenizer.draw_bbox_on_latest_picture(response, history)
   image_path = '/app/search_view_output.jpg'
   image.save(image_path)



   history = None
   response_formatted = response_denormalized.replace('ref> ', 'ref>')
   return jsonify({'response': response_formatted, 'image_url': image_path})
   
@app.route('/localization', methods=['POST'])
def localization():
   global history
   history = None
   text_input = request.json.get('text', '')
   images_base64 = request.json.get('image_context', [])
   start_image_data = base64.b64decode(images_base64[0])
   end_image_data = base64.b64decode(images_base64[1])
   start_image = Image.open(BytesIO(start_image_data))
   end_image = Image.open(BytesIO(end_image_data))
   start_image_path = '/app/localization_start.jpg'
   end_image_path = '/app/localization_end.jpg'
   start_image.save(start_image_path)
   end_image.save(end_image_path)
   query = tokenizer.from_list_format([
       {'image' : start_image_path},
       {'image' : end_image_path},
       {'text': text_input},
   ])
   response, history = model.chat(tokenizer, query=query, history=history)
   history = None
   return jsonify({'response': response, 'image_url': end_image_path})

@app.route('/localization_bbox', methods=['POST'])
def localization_bbox():
   global history
   history = None
   text_input = request.json.get('text', '')
   images_base64 = request.json.get('image_context', [])
   start_image_data = base64.b64decode(images_base64[0])
   end_image_data = base64.b64decode(images_base64[1])
   start_image = Image.open(BytesIO(start_image_data))
   end_image = Image.open(BytesIO(end_image_data))
   start_image_path = '/app/localization_start.jpg'
   end_image_path = '/app/localization_end.jpg'
   start_image.save(start_image_path)
   end_image.save(end_image_path)
   
   def denormalize_bbox(bbox):
      x0, y0 = map(int, bbox[0].strip('()').split(','))
      x1, y1 = map(int, bbox[1].strip('()').split(','))
      width, height = end_image.size
      denormalized_bbox = (
         int(x0 * width / 1000),
         int(y0 * height / 1000),
         int(x1 * width / 1000),
         int(y1 * height / 1000)
      )
      return denormalized_bbox

   query = tokenizer.from_list_format([
       {'image' : start_image_path},
       {'image' : end_image_path},
       {'text': text_input},
   ])
   response, history = model.chat(tokenizer, query=query, history=history)
      # Denormalize the bounding box coordinates in the response
   response_parts = response.split('<box>')
   for i in range(1, len(response_parts)):
      bbox_part, rest_part = response_parts[i].split('</box>', 1)
      bbox_coords = bbox_part.split('),(')
      denormalized_bbox_coords = denormalize_bbox(bbox_coords)
      response_parts[i] = f'<box>({denormalized_bbox_coords[0]},{denormalized_bbox_coords[1]}),({denormalized_bbox_coords[2]},{denormalized_bbox_coords[3]})</box>' + rest_part
   response_denormalized = ''.join(response_parts)
   image = tokenizer.draw_bbox_on_latest_picture(response, history)
   image_path = '/app/localization_bbox_view_output.jpg'
   image.save(image_path)
   history = None
   response_formatted = response_denormalized.replace('ref> ', 'ref>')
   return jsonify({'response': response_formatted, 'image_url': image_path})

if __name__ == '__main__':
   app.run(host='0.0.0.0', port=8080)
