# 🌸 Haven AI Companion Server (C#)

**Haven** is a 100% self-hosted, private, uncensored multi-modal AI companion platform created by **Daniel (Barrer Software)** and the **Haven AI Companion Community**.

---

## 🌟 Key Capabilities

* 🧠 **Neural Model Support**: Pre-configured for **Haven-Chat-v3.0** (Gemma 4 architecture, DPO aligned, zero corporate assistant disclaimers).
* 🖼️ **2-Second Custom Selfies**: Integrates Stable Diffusion LCM with character LoRAs, dynamic lighting variations, and intimate scene detection (`naked`, `undressed`, `in bed`).
* 🎮 **Interactive 3D Avatars**: Full VRM model support with automatic material unlit conversion, speech viseme lip-sync, and 3D Wardrobe Layer Control Overlays.
* 🧠 **Episodic Relationship Memory**: SQLite relationship graph storing personal facts, preferences, and milestones with automatic fact extraction.
* 📱 **Native Mobile Android App Sync**: Real-time WebSocket streaming with the `project-haven-android` companion app.

---

## 🚀 Quick Start & Installation

### Option 1: Docker (Recommended)
```bash
# Launch Haven Server in 1 command
docker compose up -d
```

### Option 2: Windows (.NET 10)
```powershell
# Build and run locally
dotnet build -c Release
bin\Release\net10.0\win-x64\haven-server.exe
```

### Option 3: Kubernetes / K3s Cluster
```bash
kubectl apply -f k8s/deployment.yaml
```

---

## 🌐 Official Ecosystem Links

* 📦 **Hugging Face Model**: [haven-ai-companion/haven-chat-v3.0](https://huggingface.co/haven-ai-companion/haven-chat-v3.0)
* 📱 **Android Client**: [Haven-AI-Companion/project-haven-android](https://github.com/Haven-AI-Companion/project-haven-android)
* 🎴 **Companion Cards**: [datasets/haven-ai-companion/haven-companion-cards](https://huggingface.co/datasets/haven-ai-companion/haven-companion-cards)
* 🎨 **SD LoRAs**: [haven-ai-companion/haven-lora-models](https://huggingface.co/haven-ai-companion/haven-lora-models)
