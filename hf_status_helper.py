import sys
import json

def main():
    status = {
        "installed": False,
        "loggedIn": False,
        "username": None
    }
    
    try:
        import huggingface_hub
        status["installed"] = True
        
        try:
            api = huggingface_hub.HfApi()
            user = api.whoami()
            if user and "name" in user:
                status["loggedIn"] = True
                status["username"] = user["name"]
        except Exception:
            pass
            
    except Exception as e:
        status["error"] = str(e)
        
    print(json.dumps(status))

if __name__ == "__main__":
    main()
