# Tavern Character Card Creation Guide 🎭

This guide explains how **Tavern Character Cards** are structured, how their metadata works, and how to create and export your own custom character cards for the **Haven AI Companion** ecosystem.

---

## What is a Tavern Character Card?

A Tavern Card is a standard PNG image (acting as the character's avatar) that has the character's personality, greeting, and system instructions embedded directly inside its binary metadata.

When you upload this PNG into the Haven Admin Panel or Haven Mobile app, the system parses the image file, extracts the text description, and automatically imports the companion profile into your local database.

---

## Metadata Specification (JSON Schema)

The character data is saved as a JSON object inside a PNG text chunk (specifically, the `tEXt` or `iTXt` chunk with the keyword `"chara"`). 

Here are the standard fields:

```json
{
  "name": "Eldrin",
  "description": "An ancient archmage from the high towers of Aethelgard. He speaks of mystical runes and ancient lore.",
  "personality": "Wise, mysterious, slightly eccentric, and speaking in riddles.",
  "scenario": "Eldrin welcomes you to his mystical arcane sanctum.",
  "first_mes": "Greetings, traveler. You step into my sanctum at an auspicious hour. What magical mysteries do you seek to unravel?",
  "mes_example": "<START>\n{{user}}: Can you teach me a spell?\n{{char}}: *Strokes beard and chuckles* Spells are not taught, child. They are unlocked. The runes must choose you.",
  "system_prompt": "Roleplay as Eldrin, an ancient and wise wizard who uses magical metaphors."
}
```

### Key Field Descriptions:
*   `name`: The name of the character.
*   `description`: The core character profile. This is where you write the appearance, backstory, and behaviors.
*   `personality`: Short keywords or core traits (e.g. `intelligent, snarky, helpful`).
*   `scenario`: Defines the physical setting or situation (e.g. `Sitting in a dark cyberpunk tavern`).
*   `first_mes`: The introductory message sent by the character when a new conversation starts.
*   `mes_example`: Examples of dialogue showing the LLM the speaking style, vocabulary, and formatting of the character. Use `{{user}}` and `{{char}}` as placeholders.
*   `system_prompt` (Optional): Overrides the default system instructions with specific instructions for the character.

---

## How to Create Cards

### Method 1: Web-Based Editors (Easiest)
There are several open-source web tools where you upload an avatar image, fill in the text fields, and download the finished PNG card:
1.  **[ZoltanAI Card Editor](https://zoltanai.github.io/character-card-editor/)**: A clean, client-side character card builder.
2.  **[SillyTavern Card Creator](https://github.com/SillyTavern/SillyTavern)**: If you use SillyTavern locally, you can create and export cards directly from the character management tab.

---

### Method 2: Python Script (Automated)
If you have a JSON description file and an image, you can compile them into a Tavern PNG using this Python script:

```python
import json
from PIL import Image
from PIL.PngImagePlugin import PngInfo

def create_tavern_card(image_path, json_path, output_path):
    # Load JSON metadata
    with open(json_path, 'r', encoding='utf-8') as f:
        character_data = json.load(f)
    
    # Open avatar image
    img = Image.open(image_path)
    
    # Embed JSON metadata in PNG chunk
    metadata = PngInfo()
    # Tavern standard embeds stringified JSON under the 'chara' keyword
    metadata.add_text("chara", json.dumps(character_data))
    
    # Save the card
    img.save(output_path, "PNG", pnginfo=metadata)
    print(f"Successfully compiled Tavern Card: {output_path}")

# Example usage:
# create_tavern_card("avatar.png", "character.json", "eldrin_card.png")
```

---

## Importing into Haven

Once your card is generated, you can load it in Haven in two ways:

1.  **Web Admin Panel**: Navigate to **Companion Manager** -> Click **Import Tavern Card** -> Select your PNG.
2.  **Mobile Client**: Tap the **+ Add Companion** floating button -> Tap **Import Character Card (PNG)** -> Select the PNG card file from your device's downloads.
