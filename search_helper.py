import sys
import json
from huggingface_hub import HfApi

def main():
    if len(sys.argv) < 2:
        print(json.dumps([]))
        return
        
    query = sys.argv[1].strip()
    api = HfApi()
    try:
        models = api.list_models(
            search=query,
            limit=15,
            sort="downloads"
        )
        
        results = []
        for m in models:
            results.append({
                "id": m.id,
                "downloads": getattr(m, "downloads", 0),
                "likes": getattr(m, "likes", 0),
                "tags": getattr(m, "tags", [])
            })
        print(json.dumps(results))
    except Exception as e:
        print(json.dumps({"error": str(e)}))

if __name__ == "__main__":
    main()
