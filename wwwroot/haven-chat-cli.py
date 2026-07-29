import urllib.request
import urllib.parse
import json
import sys

sys.stdout.reconfigure(encoding='utf-8')

server_base = "http://100.95.198.162:18799"

print("==========================================================")
print("  🌸 HAVEN COMPANION INTERACTIVE TERMINAL CLI v2.1")
print("==========================================================")

# Authenticate
login_url = f"{server_base}/api/auth/login"
login_data = urllib.parse.urlencode({
    "Username": "antigravity_bot",
    "Password": "Password123!"
}).encode('utf-8')

headers = {'Content-Type': 'application/x-www-form-urlencoded'}
token = ""

try:
    req = urllib.request.Request(login_url, data=login_data, headers=headers)
    res = urllib.request.urlopen(req)
    data = json.loads(res.read().decode('utf-8'))
    token = data.get('access_token', '')
    print(f"🔑 Session Authenticated! Server: {server_base}")
except Exception as e:
    print(f"⚠️ Auth Notice: {e}")

# Companion Selection
companion_name = input("\nEnter Companion Name (default: Sabrina): ").strip() or "Sabrina"
conv_id = f"cli_{companion_name.lower()}_session"

print(f"\n✨ Connected to companion: {companion_name}")
print("Type your message and press Enter. (Type 'exit' or 'quit' to stop)\n")
print("-" * 58)

auth_header = {'Content-Type': 'application/json', 'Authorization': f'Bearer {token}'}

while True:
    try:
        user_msg = input("\n👤 You: ").strip()
        if not user_msg:
            continue
        if user_msg.lower() in ['exit', 'quit']:
            print("\n🌸 Session ended. Have a great day!")
            break

        url = f"{server_base}/api/conversations/{conv_id}/messages"
        msg_body = json.dumps({
            "content": user_msg,
            "companionName": companion_name,
            "model_id": "haven-chat-v3.0.3"
        }).encode('utf-8')

        print(f"🌸 {companion_name}: ", end="", flush=True)

        req_msg = urllib.request.Request(url, data=msg_body, headers=auth_header)
        res_msg = urllib.request.urlopen(req_msg)
        resp_json = json.loads(res_msg.read().decode('utf-8'))
        reply = resp_json.get('content', resp_json.get('response', resp_json.get('message', str(resp_json))))
        
        print(reply)

    except KeyboardInterrupt:
        print("\n🌸 Exiting CLI...")
        break
    except Exception as ex:
        print(f"\n⚠️ Response Error: {ex}")
