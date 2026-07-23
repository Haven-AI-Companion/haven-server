import sys
import json
import struct
import os

def convert_vrm_to_unlit(input_path, output_path):
    print(f"=== Converting VRM MToon Materials to KHR_materials_unlit: {input_path} ===")
    
    try:
        with open(input_path, "rb") as f:
            data = f.read()

        if len(data) < 20:
            print("Error: File too small")
            return False

        # GLB Header Check
        magic, version, length = struct.unpack("<I I I", data[:12])
        if magic != 0x46546C67: # 'glTF'
            print(f"Error: {input_path} is not a valid GLB file.")
            return False

        # Read JSON Chunk
        chunk_len, chunk_type = struct.unpack("<I I", data[12:20])
        json_bytes = data[20:20 + chunk_len]
        gltf = json.loads(json_bytes.decode("utf-8"))

        # Track extensions
        extensions_used = gltf.get("extensionsUsed", [])
        if "KHR_materials_unlit" not in extensions_used:
            extensions_used.append("KHR_materials_unlit")
        gltf["extensionsUsed"] = extensions_used

        # Convert Materials
        materials = gltf.get("materials", [])
        converted_count = 0

        for mat in materials:
            extensions = mat.get("extensions", {})
            if "VRMC_materials_mtoon" in extensions or "VRM" in extensions:
                extensions["KHR_materials_unlit"] = {}
                pbr = mat.setdefault("pbrMetallicRoughness", {})
                pbr["metallicFactor"] = 0.0
                pbr["roughnessFactor"] = 1.0
                converted_count += 1

        print(f"Converted {converted_count} MToon materials to KHR_materials_unlit.")

        # Re-pack GLB
        new_json_bytes = json.dumps(gltf, separators=(',', ':')).encode("utf-8")
        while len(new_json_bytes) % 4 != 0:
            new_json_bytes += b' '

        new_chunk_len = len(new_json_bytes)
        bin_chunk = data[20 + chunk_len:]

        new_total_len = 12 + 8 + new_chunk_len + len(bin_chunk)
        new_header = struct.pack("<I I I", magic, version, new_total_len)
        new_json_header = struct.pack("<I I", new_chunk_len, 0x4E4F534A)

        with open(output_path, "wb") as f:
            f.write(new_header)
            f.write(new_json_header)
            f.write(new_json_bytes)
            f.write(bin_chunk)

        print(f"Successfully saved converted model to: {output_path}")
        return True
    except Exception as e:
        print(f"Error converting VRM: {e}")
        return False

if __name__ == "__main__":
    if len(sys.argv) > 2:
        convert_vrm_to_unlit(sys.argv[1], sys.argv[2])
    elif len(sys.argv) == 2:
        convert_vrm_to_unlit(sys.argv[1], sys.argv[1])
    else:
        print("Usage: python convert_vrm_to_unlit.py <input.glb> [output.glb]")
