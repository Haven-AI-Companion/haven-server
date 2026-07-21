import sys
import os
import argparse

# Monkey-patch tqdm inside huggingface_hub to intercept progress reporting
try:
    from tqdm import tqdm
    import huggingface_hub.file_download
    
    class ProgressTqdm(tqdm):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, **kwargs)
            self._last_percent = 0.0

        def update(self, n=1):
            super().update(n)
            if self.total:
                percent = (self.n / self.total) * 100
                # Reduce print spam by only printing on significant progress changes
                if percent - self._last_percent >= 0.5 or percent >= 100.0:
                    print(f"PROGRESS: {percent:.1f}%", flush=True)
                    self._last_percent = percent
                    
        def close(self):
            super().close()
            print("PROGRESS: 100.0%", flush=True)

    huggingface_hub.file_download.tqdm = ProgressTqdm
except Exception as e:
    # Fallback to standard logging if patch fails
    pass

from huggingface_hub import hf_hub_download

def main():
    parser = argparse.ArgumentParser(description="Haven Model & LoRA Downloader Helper")
    parser.add_argument("--repo-id", required=True, help="Hugging Face Repository ID")
    parser.add_argument("--filename", required=True, help="Filename inside the repository")
    parser.add_argument("--dest-dir", required=True, help="Destination directory path")
    
    args = parser.parse_args()
    
    repo_id = args.repo_id.strip()
    filename = args.filename.strip()
    dest_dir = args.dest_dir.strip()
    
    print(f"STATUS: Initializing download for {repo_id}/{filename}...", flush=True)
    os.makedirs(dest_dir, exist_ok=True)
    
    try:
        local_path = hf_hub_download(
            repo_id=repo_id,
            filename=filename,
            local_dir=dest_dir,
            local_dir_use_symlinks=False
        )
        
        # Automatically flatten file hierarchy: if the downloaded file is nested in subfolders,
        # move it to the root dest_dir so sidecars (llama-server/sd-server) can find it immediately.
        basename = os.path.basename(local_path)
        final_dest_path = os.path.join(dest_dir, basename)
        if os.path.abspath(local_path) != os.path.abspath(final_dest_path):
            import shutil
            print(f"Flattening directory structure: moving {local_path} to {final_dest_path}...", flush=True)
            if os.path.exists(final_dest_path):
                os.remove(final_dest_path)
            shutil.move(local_path, final_dest_path)
            
            # Clean up empty parent subdirectories
            parent_dir = os.path.dirname(local_path)
            while parent_dir and os.path.abspath(parent_dir) != os.path.abspath(dest_dir):
                try:
                    if not os.listdir(parent_dir):
                        os.rmdir(parent_dir)
                    parent_dir = os.path.dirname(parent_dir)
                except Exception:
                    break
            local_path = final_dest_path

        print(f"SUCCESS: Saved file to {local_path}", flush=True)
        sys.exit(0)
    except Exception as e:
        print(f"ERROR: {str(e)}", file=sys.stderr, flush=True)
        sys.exit(1)

if __name__ == "__main__":
    main()
