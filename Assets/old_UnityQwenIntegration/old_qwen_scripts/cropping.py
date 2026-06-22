import os
import torch
import torchvision.transforms as transforms
from PIL import Image

def transform_images(input_folder, output_folder, target_size=(640, 640)):
    os.makedirs(output_folder, exist_ok=True)

    # Define transformation to resize and convert images to tensors
    transform = transforms.Compose([
        transforms.Resize(target_size),
        transforms.ToTensor(),
        transforms.Lambda(lambda x: x[:3, :, :])  # Remove alpha channel if present
    ])

    # Iterate over images in the input folder
    for filename in os.listdir(input_folder):
        if filename.endswith(('.jpg', '.jpeg', '.png')):
            input_path = os.path.join(input_folder, filename)
            output_path = os.path.join(output_folder, filename)

            try:
                # Open image
                image = Image.open(input_path)

                # Apply transformation
                transformed_image = transform(image)

                # Ensure tensor has correct shape
                if transformed_image.shape != (3, target_size[1], target_size[0]):  # Channels x Height x Width
                    raise ValueError(f"Transformed image shape is incorrect: {transformed_image.shape}")

                # Save transformed image tensor
                torch.save(transformed_image, output_path)

                print(f"Transformed and saved {filename}")
            except Exception as e:
                print(f"Error processing {filename}: {e}")

# Example usage:
input_folder = "/app/SAFE_ROAD_jpg/train"
output_folder = "/app/SAFE_ROAD_jpg/train_mod"
target_size = (640, 640)
transform_images(input_folder, output_folder, target_size)