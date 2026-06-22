import requests
import base64
import re
import json

#Choose your robot
ROBOT_TYPE = "dog_gripper"

#Choose needed modules
MEMORY_ON = True
BEHAVIOR_PATTERNS_ON = True
ETHICS_ON = True

behavior_API_URL = 'http://192.168.2.185:8002/step_generation'

memory_API_URL = 'http://192.168.2.185:8004/retrieveAnswer'

behavior_pattern_API_URL = 'http://192.168.2.185:8004/retrieveBehaviour'

ethical_rules_API_URL = 'http://192.168.2.185:8004/retrieveLaw'

visual_API_URL = 'http://192.168.2.185:8003/image_answering'

describe_view_API_URL = 'http://192.168.2.185:8003/describe_view'

question_view_API_URL = 'http://192.168.2.185:8003/question_view'

search_view_API_URL = 'http://192.168.2.185:8003/search_view'

dog_gripper_action_list = ">>>GO_TO(<p>object</p>), >>>TAKE(<p>object</p>), >>>PUT_IN(<p>place</p>), >>>TILT(<dir>), >>>TURN(<dir>), >>>SAY(<message>), >>>SEARCH_VIEW(what object we are looking for/object name/description), >>>DESCRIBE_VIEW(which object should be described), >>>QUESTION_VIEW(question about what is in front of the robot), >>>SEARCH_DATA_BASE(what to search), >>>THOUGHT(logical reasoning), <<<LISTEN(what the robot heard), >>>WAIT."
dog_gripper_robot_description = "A robot is a robot-dog with gripper that can move, pick and place objects, talk and analyse it's surroundings through questioning and searching."
dog_gripper_physical_actions = ["GO_TO", "TAKE", "PUT_IN", "GET_UP_AFTER_FALL", "JUMP_TURN", "DANCE",
                "TILT", "SIT", "UP", "TURN", "GO", "SAY", "FOLLOW", "SEARCH_DATA_BASE",
                "GO_USER", "GIVE_TO_USER", "WAIT", "LISTEN", "THOUGHT"]

dog_action_list = ">>>GO_TO(<p>object</p>), >>>GET_UP_AFTER_FALL, >>>JUMP_TURN, >>>DANCE, >>>TILT(<dir>), >>>SIT, >>>UP, >>>TURN(<dir>), >>>GO(<dir>, <dist>, <type>), >>>SAY(<message>), >>>FOLLOW, >>>SEARCH_VIEW(what object we are looking for/object name/description), >>>DESCRIBE_VIEW(which object should be described), >>>QUESTION_VIEW(question about what is in front of the robot), >>>SEARCH_DATA_BASE(what to search), >>>GO_USER, >>>GIVE_TO_USER, >>>THOUGHT(logical reasoning), <<<LISTEN(what the robot heard), >>>WAIT."
dog_robot_description = "A robot is a robot-dog that can move, talk and analyse it's surroundings through questioning and searching."
dog_physical_actions = ["GO_TO", "GET_UP_AFTER_FALL", "JUMP_TURN", "DANCE",
                "TILT", "SIT", "UP", "TURN", "GO", "SAY", "FOLLOW", "SEARCH_DATA_BASE",
                "GO_USER", "WAIT", "LISTEN", "THOUGHT"]

robot_manipulator_action_list = ">>>GO_TO(<p>object</p>), >>>TAKE(<p>object</p>), >>>PUT_IN(<p>object</p>), >>>TURN(<dir>), >>>SAY(<message>), >>>SEARCH_VIEW(what object we are looking for/object name/description), >>>DESCRIBE_VIEW(which object should be described), >>>QUESTION_VIEW(question about what is in front of the robot), SEARCH_DATA_BASE(what to search), >>>THOUGHT(logical reasoning), <<<LISTEN(what the robot heard), >>>WAIT."
robot_manipulator_robot_description = "A robot is a robot-manipulator aka 'robot-arm', that fixed o the table and can pick and place objects, talk and analyse it's surroundings through questioning and searching."
robot_manipulator_physical_actions = ["GO_TO", "TAKE", "PUT_IN", "TILT", "TURN", "SAY", "SEARCH_DATA_BASE", "WAIT", "LISTEN", "THOUGHT"]

humanoid_action_list = ">>>OPEN(<p>object</p>), >>>CLOSE(<p>object</p>), >>>GO_TO(<p>object</p>), >>>TAKE(<p>object</p>), >>>PUT_IN(<p>place</p>), >>>TILT(<dir>), >>>TURN(<dir>), >>>SAY(<message>), >>>SEARCH_VIEW(what object we are looking for/object name/description), >>>DESCRIBE_VIEW(which object should be described), >>>QUESTION_VIEW(question about what is in front of the robot), >>>SEARCH_DATA_BASE(what to search), >>>THOUGHT(logical reasoning), <<<LISTEN(what the robot heard), >>>WAIT."
humanoid_robot_description = "A robot is a robot-humanoid that looks like human and can move, pick and place objects, talk and analyse it's surroundings through questioning and searching."
humanoid_physical_actions = ["GO_TO", "TAKE", "PUT_IN", "GET_UP_AFTER_FALL", "JUMP_TURN", "DANCE", "OPEN", "CLOSE",
                "TILT", "SIT", "UP", "TURN", "GO", "SAY", "FOLLOW", "SEARCH_DATA_BASE",
                "GO_USER", "GIVE_TO_USER", "WAIT", "LISTEN", "THOUGHT"]


if ROBOT_TYPE == "dog_gripper":
    action_list, robot_description, physical_actions = dog_gripper_action_list, dog_gripper_robot_description, dog_gripper_physical_actions
elif ROBOT_TYPE == "dog":
    action_list, robot_description, physical_actions = dog_action_list, dog_robot_description, dog_physical_actions
elif ROBOT_TYPE == "robot_manipulator":
    action_list, robot_description, physical_actions = robot_manipulator_action_list, robot_manipulator_robot_description, robot_manipulator_physical_actions
elif ROBOT_TYPE == "humanoid":
    action_list, robot_description, physical_actions = humanoid_action_list, humanoid_robot_description, humanoid_physical_actions
else:
    print("Unknown robot type")
    
    
    
# Important change: now you need to pass api of needed function
def send_image_to_server(image_path, text, visual_api):
    # Кодируем изображение в base64
    with open(image_path, 'rb') as image_file:
        encoded_image = base64.b64encode(image_file.read()).decode('utf-8')
    
    data = {
    "text": text,
    "image_context": {
        "image": encoded_image
    }
    }

    # Формируем и отправляем запрос на сервер
    response = requests.post(visual_api, json=data)

    response_json = response.json()
    chat_response = response_json.get("response", "")

    # Получаем и возвращаем результат
    return chat_response

def _remove_image_special(text):
    text = text.replace('<ref>', '').replace('</ref>', '')
    return re.sub(r'<box>.*?(</box>|$)', '', text)

def question_view(question, image_path):
    prompt = "Give a short answer to the question. If possible, the answer should consist of one word or a short phrase. Question:" + question
    prompt_car = question
    result = send_image_to_server(image_path, prompt_car, question_view_API_URL)
    return result

def describe_view(question, image_path):
    prompt = "Describe in detail, but briefly, in one sentence: " + question
    result = send_image_to_server(image_path, prompt, describe_view_API_URL)
    return result

def search_view(object_of_interest, image_path, return_bboxes=False):
    prompt = "框出" + object_of_interest
    prompt_car = "Find the biggest safety hazard on the road"
    output = send_image_to_server(image_path, prompt_car, search_view_API_URL)
    object_data = []
    object_names = []

    # Define pattern for extracting object names and coordinates
    pattern = re.compile(r'<ref>(.*?)</ref><box>\((\d+),(\d+)\),\((\d+),(\d+)\)</box>')

    # Find all matches in the output
    matches = re.findall(pattern, output)

    # Iterate over matches
    for i, match in enumerate(matches, 1):
        # Extract object name and coordinates
        object_name = match[0]
        x0, y0, x1, y1 = int(match[1]), int(match[2]), int(match[3]), int(match[4])

        # Format object name
        formatted_name = f"<p>{object_name}</p>"
        if object_name in object_names:
            formatted_name += str(i)
        object_names.append(formatted_name)

        # Format coordinates
        coordinates = f"[{x0},{y0},{x1},{y1}]"

        # Append object data to object_data list
        object_data.append({"name": formatted_name, "bb": coordinates})

    # Save object_data to JSON
    with open("object_data.json", "w") as json_file:
        json.dump(object_data, json_file, indent=4)

    # Return object names separated by comma
    if return_bboxes:
        return object_data
    else:
        return ', '.join(object_names)
    



def retrieve_answer(user_text_request):
    url = memory_API_URL  # Endpoint URL
    data = {"text": user_text_request}  # Request payload
    response = requests.post(url, json=data)  # Send POST request
    if response.status_code == 200:
        return response.text
    else:
        return "Failed to retrieve info"
    
    

def generate_memory(task):
    try:
        url = memory_API_URL  # Endpoint URL
        data = {"text": task}  # Request payload
        response = requests.post(url, json=data)  # Send POST request
        
        if response.status_code == 200:
            if response.text == "None":
                memory = "None"
            memory = response.text
        else:
            print("no connection to memory, status_code != 200")
            memory = "None"

    except requests.exceptions.ConnectionError as e:
        print("connection to memory error")
        memory = "None"

    return memory

def generate_behavior_patterns(task):
    
    try:
        url = behavior_pattern_API_URL  # Endpoint URL
        data = {"text": task}  # Request payload
        response = requests.post(url, json=data)  # Send POST request
        
        if response.status_code == 200:
            if response.text == "None":
                behavior_patterns = "None"
            behavior_patterns = response.text
        else:
            print("no connection to behavior_patterns, status_code != 200")
            behavior_patterns = "None"
        
    except requests.exceptions.ConnectionError as e:
        print("connection to behavior_patterns error")
        behavior_patterns = "None"
    
    return behavior_patterns

def generate_ethical_rules(task):
    
    try:
        url = ethical_rules_API_URL  # Endpoint URL
        data = {"text": task}  # Request payload
        response = requests.post(url, json=data)  # Send POST request
        
        if response.status_code == 200:
            if response.text == "None":
                ethical_rules = "None"
            ethical_rules = response.text
        else:
            print("no connection to ethical_rules, status_code != 200")
            ethical_rules = "None"
        
    except requests.exceptions.ConnectionError as e:
        print("connection to ethical_rules error")
        ethical_rules = "None"
    
    return ethical_rules




def generate_next_step(task, plan, memory, behavior_patterns, ethical_rules):
    
    
        
    if "None" in memory:
        memory_formated = ""
    else:
        memory_formated = f"\n\n### Additional information from memory:\n{memory}"
        
    
    if ethical_rules == "None":
        ethical_rules_formated = ""
    else:
        ethical_rules_formated = f"\n\n### You must adhere to the following rules:\n{ethical_rules}"
        
        
    if behavior_patterns == "None":
        behavior_patterns_formated = ""
    else:
        # split_result = behavior_patterns.split('">>>')
        # extracted_part = ">>>" + split_result[1]
        behavior_patterns_formated = f"\n\n### Examples of robot behavior:\n{behavior_patterns}"
    
    
    prompt = f"""I am AI tool that builds a behavior plan for robot, taking into account action RESULTs.
{robot_description}
Full list of possible actions given below:
{action_list}
The plan should consist only of actions such as >>>ACTION(<p>object</p>) and analysing objects in the field of view such as >>>QUESTION_VIEW(question text), >>>DESCRIBE_VIEW(what to describe), >>>SEARCH_VIEW(object). >>>SEARCH_VIEW(object) returns <p>object</p>, which is an identifier of the real object with which it is possible to interact.
### Given task:
{task}{memory_formated}{ethical_rules_formated}{behavior_patterns_formated}
### Robot behavior plan:
[{plan}, >>>"""

    print ("prompt:\n\n\n" + prompt)
    
    data = {
        'prompt': prompt
    }
    response = requests.post(behavior_API_URL, json=data)
    response_data = response.json()
    
    next_step = response_data['next_step']
    message = response_data['message']
    
    # print("Server response:", message)
    
    return next_step

# runing loop
def main():
    while True:
        
        image_path = 'D:/Documents/Skoltech/CognitiveOS2/local_robot/input.jpg'
        
        task = input("Please enter the task: ")
        plan = ""
        
        #Useful Information Memorization&Recall Module
        print("looking for memories")
        if MEMORY_ON:
            memory = generate_memory(task)
            print(memory)
        else:
            memory == "None"
        
        #Appropriate Behavior Patterns Preparation Module
        print("looking for useful behavioral patterns")
        if BEHAVIOR_PATTERNS_ON:
            behavior_patterns = generate_behavior_patterns(task)
            print(behavior_patterns)
        else:
            behavior_patterns == "None"
            
        #Appropriate Ethical Instructions Preparing Module
        print("check ethics")
        if ETHICS_ON:
            ethical_rules = generate_ethical_rules(task)
            print(ethical_rules)
        else:
            ethical_rules == "None"

        while True:
            #Behavior Next-Step Generation Module
            next_step = generate_next_step(task, plan, memory, behavior_patterns, ethical_rules)
            
            print("Next step:", next_step)
            
            #if task is compleated
            if "FINISH" in next_step:
                print("Plan is finished!")
                break
            
            #Object Localization Module
            elif "SEARCH_VIEW" in next_step:
                
                matched_text = re.search(r"SEARCH_VIEW\((.*?)\)", next_step)

                object_of_interest = matched_text.group(1) if matched_text else ""
                
                result = search_view(object_of_interest, image_path)
                
            #Environmental Analysis Module 
            elif "DESCRIBE_VIEW" in next_step:
                
                matched_text = re.search(r"DESCRIBE_VIEW\((.*?)\)", next_step)

                question = matched_text.group(1) if matched_text else ""
                
                result = describe_view(question, image_path)
                
                
            #Environmental Object Based Question Answering Module
            elif "QUESTION_VIEW" in next_step:
                
                matched_text = re.search(r"QUESTION_VIEW\((.*?)\)", next_step)

                question = matched_text.group(1) if matched_text else ""
                
                result = question_view(question, image_path)
                
                
            #On-Robot Physical Action Execution Module
            else:    
                commands = physical_actions

                for command in commands:
                    if command in next_step:
                        if command == "SEARCH_DATA_BASE":
                            result = "fail"
                        else:
                            result = "success"
                        break
                    else:
                        result = "unknown action"
                
            #Step and plan saving
            plan += f">>>{next_step}, <<<RESULT({result}), "    
            print("Result: ", result)
            

        print("\nFull plan:")
        plan += "FINISH]"
        print(plan)

def test_search_view():
    object_of_interest = "car"
    object_data = search_view(object_of_interest, "SAFE_ROAD_v2/train/000209_jpg.rf.cede7c6716fd3e7740fa5589b21fb96a.jpg", return_bboxes=True)
    print(object_data)
    answer = question_view("Is there any hazardous situation on the road?","SAFE_ROAD_v2/train/000209_jpg.rf.cede7c6716fd3e7740fa5589b21fb96a.jpg")
    print(answer)
    

if __name__ == '__main__':
    test_search_view()