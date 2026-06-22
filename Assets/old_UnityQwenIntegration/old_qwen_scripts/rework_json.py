import json

def transform_json(input_file, output_file):
    with open(input_file, 'r') as f:
        data = json.load(f)

    for entry in data:
        for conversation in entry['conversations']:
            if conversation['from'] == 'user':
                # Transform the value for 'user'
                img_name = conversation['value'].split('<img>')[1].split('.jpg')[0]
                conversation['value'] = f"<img>{img_name}.jpg</img>\u0436\u040e\u2020\u0435\u2021\u0454Find the biggest safety hazard on the road."

    # Write the transformed data to a new JSON file
    with open(output_file, 'w') as f:
        json.dump(data, f, indent=4)

input_file = 'safe_road_train.json'
output_file = 'safe_road_mod.json'
transform_json(input_file, output_file)