import sys
import json
from huggingface_hub import HfApi

def main():
    if len(sys.argv) < 2:
        print(json.dumps([]))
        return
        
    repo_id = sys.argv[1].strip()
    api = HfApi()
    try:
        files = api.list_repo_files(repo_id)
        # Filter model files
        filtered = [f for f in files if f.endswith(('.safetensors', '.gguf', '.bin', '.ckpt'))]
        print(json.dumps(filtered))
    except Exception as e:
        print(json.dumps({"error": str(e)}))

if __name__ == "__main__":
    main()
