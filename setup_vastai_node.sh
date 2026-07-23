#!/bin/bash
# 🌸 Haven AI Companion - Vast.ai GPU Node Auto-Setup Script
# Usage: curl -sSL https://raw.githubusercontent.com/Haven-AI-Companion/haven-server/main/setup_vastai_node.sh | bash

echo "=========================================================="
echo "   🌸 Haven AI Companion - Vast.ai GPU Node Auto-Setup   "
echo "=========================================================="

# 1. Update packages and install dependencies
apt-get update && apt-get install -y wget curl git build-essential cmake aria2 unzip netbird

# 2. Download llama.cpp binary if needed
mkdir -p ~/bin ~/models
if [ ! -f ~/bin/llama-server ]; then
    echo "[Haven Setup] Installing llama.cpp binary..."
    wget -q https://github.com/ggml-org/llama.cpp/releases/download/b3600/llama-b3600-bin-ubuntu-x64.zip -O ~/llama.zip
    unzip -o ~/llama.zip -d ~/bin/
    rm ~/llama.zip
fi

# 3. Download Haven-Chat-v3.0.1.gguf model
MODEL_PATH=~/models/haven-chat-v3.0.1.gguf
if [ ! -f "$MODEL_PATH" ]; then
    echo "[Haven Setup] Multi-thread downloading Haven-Chat-v3.0.1.gguf (5.09 GB) from Hugging Face..."
    aria2c -x 16 -s 16 -k 1M https://huggingface.co/haven-ai-companion/haven-chat-v3.0.1/resolve/main/haven-chat-v3.0.1.gguf -d ~/models -o haven-chat-v3.0.1.gguf
fi

# 4. Download stable-diffusion.cpp sd-server binary and DreamShaper SD1.5 model
SD_MODEL_PATH=~/models/DreamShaper_8_pruned.safetensors
if [ ! -f "$SD_MODEL_PATH" ]; then
    echo "[Haven Setup] Downloading DreamShaper SD1.5 model for fast GPU selfies..."
    wget -q https://huggingface.co/Lykon/DreamShaper/resolve/main/DreamShaper_8_pruned.safetensors -O "$SD_MODEL_PATH"
fi

if [ ! -f ~/bin/sd-server ]; then
    echo "[Haven Setup] Downloading sd-server binary..."
    wget -q https://github.com/leejet/stable-diffusion.cpp/releases/download/master/sd-master-bin-ubuntu-x64.zip -O ~/sd.zip 2>/dev/null || true
    unzip -o ~/sd.zip -d ~/bin/ 2>/dev/null || true
    rm ~/sd.zip 2>/dev/null || true
fi

# 5. Launch llama-server and sd-server on GPU
echo "[Haven Setup] Launching llama-server (LLM) on GPU port 11436..."
pkill -f llama-server 2>/dev/null
nohup ~/bin/llama-server \
    --model "$MODEL_PATH" \
    --alias haven-chat-v3.0.1 \
    --host 0.0.0.0 \
    --port 11436 \
    --threads 8 \
    --ctx-size 16384 \
    --n-gpu-layers 999 \
    --flash-attn on > ~/llama_server.log 2>&1 &

if [ -f ~/bin/sd-server ]; then
    echo "[Haven Setup] Launching sd-server (Stable Diffusion LCM) on GPU port 18790..."
    pkill -f sd-server 2>/dev/null
    nohup ~/bin/sd-server \
        -m "$SD_MODEL_PATH" \
        --port 18790 \
        --listen \
        --type f16 > ~/sd_server.log 2>&1 &
fi

# 6. Check NetBird Connection
if command -v netbird >/dev/null 2>&1; then
    if ! netbird status 2>/dev/null | grep -q "Connected"; then
        MGMT_FLAG=""
        if [ -n "$NETBIRD_MGMT_URL" ]; then
            MGMT_FLAG="--management-url $NETBIRD_MGMT_URL"
        fi
        if [ -n "$NETBIRD_KEY" ]; then
            echo "[Haven Setup] Connecting self-hosted NetBird mesh network..."
            netbird up $MGMT_FLAG --key "$NETBIRD_KEY"
        else
            echo "[Haven Setup] NetBird is installed. Run 'netbird up $MGMT_FLAG --key <YOUR_KEY>' to connect your private mesh!"
        fi
    fi
fi

sleep 3
echo "=========================================================="
echo "   ✅ Vast.ai GPU Node is ONLINE and serving v3.0.1!    "
echo "   • LLM Port: 11436 (Haven-Chat-v3.0.1)                "
echo "   • SD Selfie Port: 18790 (DreamShaper SD1.5)           "
echo "=========================================================="
