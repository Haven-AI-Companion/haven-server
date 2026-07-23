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

# 4. Launch llama-server on GPU
echo "[Haven Setup] Launching llama-server on GPU port 11436..."
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

sleep 3
echo "=========================================================="
echo "   ✅ Vast.ai GPU Node is ONLINE and serving v3.0.1!    "
echo "   Listening on port: 11436                              "
echo "=========================================================="
