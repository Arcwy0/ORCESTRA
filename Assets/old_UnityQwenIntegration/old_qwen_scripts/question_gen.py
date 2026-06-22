import json
import random

# Загрузка данных из JSON файла
with open("objects_updated.json", "r") as file:
    data = json.load(file)

# Генерация 1000 семплов
num_samples = 1000
samples = []

for i in range(num_samples):
    # Выбор случайного изображения
    image_data = random.choice(data["images"])
    image_name = image_data["file_name"]

    # Генерация случайного вопроса
    object_data = random.choice(image_data["object"])
    object_name = object_data["name"]
    attributes = random.sample(object_data["attributes"], min(2, len(object_data["attributes"])))

    question_type = random.randint(0, 2)
    if question_type == 0:
        # Вопрос про наличие объекта
        question = f"is there a {object_name}?"
        answer = "yes" if random.random() < 0.8 else "no"  # Увеличиваем вероятность ответа "no"
    elif question_type == 1:
        # Вопрос про наличие атрибутов у объекта
        question = f"is there {' '.join(attributes)} food?"
        answer = "yes" if any(attr in attributes for attr in object_data["attributes"]) else "no"
    else:
        # Вопрос про объекты с определенными атрибутами
        matching_objects = [obj["name"] for obj in image_data["object"] if all(attr in obj["attributes"] for attr in attributes)]
        if matching_objects:
            question = f"what {' '.join(attributes)} food is there?"
            answer = ", ".join(matching_objects)
        else:
            question = f"what {' '.join(attributes)} food is there?"
            answer = "no"

    # Формирование сэмпла
    sample = {
        "id": f"identity_{i}",
        "conversations": [
            {"from": "user", "value": f"<img>{image_name}</img>\n{question}"},
            {"from": "assistant", "value": answer}
        ]
    }
    samples.append(sample)

# Запись сгенерированных сэмплов в JSON файл
with open("food_vqa_dataset.json", "w") as file:
    json.dump(samples, file, indent=2)
