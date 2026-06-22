import json

# Read the JSON file
with open('food_vqa_dataset.json', 'r') as file:
    data = json.load(file)

# Iterate through each item in the JSON data
for item in data:
    # Iterate through each conversation in the item
    for conversation in item['conversations']:
        # Check if the 'value' field contains an <img></img> box
        if '<img>' in conversation['value'] and '</img>' in conversation['value']:
            # Modify the 'value' field to include "/app/data/train_full/"
            conversation['value'] = conversation['value'].replace('<img>', '<img>/app/ds_generator/res_food/')

# Write the modified data back to the JSON file
with open('food_data.json', 'w') as file:
    json.dump(data, file, indent=4)

print("Modification complete!")