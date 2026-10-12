#!/usr/bin/env python3
"""Rebuild a simple UI thumbnail in an existing iOS AssetBundle template.

This is intentionally limited to single-Sprite/single-Texture2D bundles. It
keeps the donor's iOS SerializedFile and AssetBundle structure, copies the
source Sprite's geometry and RGBA pixels, and rewires the Sprite texture to
the donor's internal Texture2D. It refuses extra asset classes or references.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.util
import json
import sys
from pathlib import Path

import UnityPy
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
HELPER_PATH = ROOT / "scripts" / "build-action-asset-bundles.py"
spec = importlib.util.spec_from_file_location("octo_helpers", HELPER_PATH)
octo = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(octo)


def one(env, kind):
    values = [obj for obj in env.objects if obj.type.name == kind]
    if len(values) != 1:
        raise ValueError(f"expected exactly one {kind}, found {len(values)}")
    return values[0]


def serialized_file(env):
    files = {id(obj.assets_file): obj.assets_file for obj in env.objects}
    if len(files) != 1:
        raise ValueError(f"expected one SerializedFile, found {len(files)}")
    return next(iter(files.values()))


def pointer_is_null(ptr):
    return ptr is None or int(getattr(ptr, "m_PathID", 0)) == 0


def clone_serialized(value):
    """Clone UnityPy value records without traversing PPtr->SerializedFile graphs."""
    if value is None or isinstance(value, (str, int, float, bool, bytes, bytearray)):
        return value
    if isinstance(value, list):
        return [clone_serialized(item) for item in value]
    if isinstance(value, tuple):
        return tuple(clone_serialized(item) for item in value)
    if isinstance(value, dict):
        return {clone_serialized(k): clone_serialized(v) for k, v in value.items()}
    if type(value).__name__ == "PPtr":
        return value
    result = copy.copy(value)
    names = set()
    if hasattr(value, "__dict__"):
        names.update(vars(value))
    for cls in type(value).__mro__:
        slots = getattr(cls, "__slots__", ())
        if isinstance(slots, str):
            slots = (slots,)
        names.update(slots)
    for name in names:
        if name.startswith("_") or not hasattr(value, name):
            continue
        try:
            setattr(result, name, clone_serialized(getattr(value, name)))
        except (AttributeError, TypeError):
            pass
    return result


def normalize(value, *, texture_ptr=False):
    """Stable structural value for round-trip validation, without object ids."""
    if texture_ptr:
        return "<internal-texture>"
    if type(value).__name__ == "PPtr":
        return {"file_id": int(value.m_FileID), "path_id": int(value.m_PathID)}
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    if isinstance(value, (bytes, bytearray)):
        return {"bytes": len(value), "sha256": hashlib.sha256(value).hexdigest()}
    if isinstance(value, (list, tuple)):
        return [normalize(item) for item in value]
    if isinstance(value, dict):
        return {str(k): normalize(v) for k, v in sorted(value.items(), key=lambda p: str(p[0]))}
    fields = {}
    if hasattr(value, "__dict__"):
        fields.update({k: v for k, v in vars(value).items() if not k.startswith("_")})
    for cls in type(value).__mro__:
        slots = getattr(cls, "__slots__", ())
        if isinstance(slots, str):
            slots = (slots,)
        for key in slots:
            if key.startswith("_") or key in fields or not hasattr(value, key):
                continue
            fields[key] = getattr(value, key)
    if fields:
        return {k: normalize(v) for k, v in sorted(fields.items())}
    return repr(value)


def sprite_signature(sprite):
    attrs = [name for name in dir(sprite) if name.startswith("m_") and name != "m_Name"]
    result = {}
    for name in attrs:
        value = getattr(sprite, name)
        if name == "m_RD":
            rd = {}
            rd_names = [key for key in dir(value) if key.startswith("m_") or key in {
                "texture", "alphaTexture", "textureRect", "textureRectOffset", "settingsRaw", "uvTransform"
            }]
            for key in rd_names:
                if key.startswith("_") or not hasattr(value, key):
                    continue
                item = getattr(value, key)
                rd[key] = normalize(item, texture_ptr=(key == "texture"))
            result[name] = rd
        else:
            result[name] = normalize(value)
    return result


def assert_no_hidden_sprite_references(sprite):
    rd = sprite.m_RD
    if not pointer_is_null(rd.alphaTexture):
        raise ValueError("source Sprite has a separate alphaTexture dependency")
    for name in ("m_SpriteAtlas",):
        ptr = getattr(sprite, name, None)
        if not pointer_is_null(ptr):
            raise ValueError(f"source Sprite references {name}")
    for name in ("m_ScriptableObjects", "m_Bones"):
        value = getattr(sprite, name, None)
        if value:
            raise ValueError(f"source Sprite has non-empty {name}; dependency remap is out of scope")


def make_bundle(source_bytes: bytes, donor_bytes: bytes, logical_name: str) -> tuple[bytes, dict]:
    if not logical_name.startswith("ui/") or not logical_name.endswith(".unity3d"):
        raise ValueError("logical name must be a ui/ .unity3d path")
    src_env = UnityPy.load(octo.octo_to_unityfs(source_bytes))
    dst_env = UnityPy.load(octo.octo_to_unityfs(donor_bytes))
    src_file = serialized_file(src_env)
    dst_file = serialized_file(dst_env)
    if src_file.unity_version != "2018.4.11f1" or dst_file.unity_version != "2018.4.11f1":
        raise ValueError(f"Unity version mismatch: source={src_file.unity_version}, donor={dst_file.unity_version}")
    if dst_file.target_platform != 9:
        raise ValueError(f"donor target_platform must be iOS(9), got {dst_file.target_platform}")
    src_types = sorted({obj.type.name for obj in src_env.objects})
    dst_types = sorted({obj.type.name for obj in dst_env.objects})
    if src_types != ["AssetBundle", "Sprite", "Texture2D"] or dst_types != ["AssetBundle", "Sprite", "Texture2D"]:
        raise ValueError(f"unexpected classes: source={src_types}, donor={dst_types}")
    if src_file.externals or dst_file.externals:
        raise ValueError("external SerializedFile references are unsupported")

    src_sprite = one(src_env, "Sprite").read()
    src_tex = one(src_env, "Texture2D").read()
    dst_sprite_obj = one(dst_env, "Sprite")
    dst_tex_obj = one(dst_env, "Texture2D")
    dst_sprite = dst_sprite_obj.read()
    dst_tex = dst_tex_obj.read()
    assert_no_hidden_sprite_references(src_sprite)
    assert_no_hidden_sprite_references(dst_sprite)

    image = src_tex.image.convert("RGBA")
    leaf = logical_name.rsplit("/", 1)[1].removesuffix(".unity3d")

    # Copy all serialized Sprite fields except reader ownership. The source and
    # donor share Unity 2018's type tree; the only permitted PPtr is the texture.
    donor_texture_ptr = dst_sprite.m_RD.texture
    for name in [key for key in dir(src_sprite) if key.startswith("m_")]:
        if name == "m_Name":
            continue
        setattr(dst_sprite, name, clone_serialized(getattr(src_sprite, name)))
    dst_sprite.m_Name = leaf
    dst_sprite.m_RD.texture = donor_texture_ptr
    dst_sprite.save()

    dst_tex.m_Name = leaf
    dst_tex.m_Width = image.width
    dst_tex.m_Height = image.height
    dst_tex.m_TextureFormat = 4  # RGBA32; UnityPy writes the decoded source pixels inline.
    dst_tex.m_MipCount = 1
    dst_tex.m_CompleteImageSize = image.width * image.height * 4
    if getattr(dst_tex, "m_StreamData", None) is not None:
        dst_tex.m_StreamData.path = ""
        dst_tex.m_StreamData.offset = 0
        dst_tex.m_StreamData.size = 0
    dst_tex.image = image
    dst_tex.save()

    bundle_obj = one(dst_env, "AssetBundle")
    tree = bundle_obj.read_typetree()
    old_key = str(tree["m_Name"])
    old_leaf = old_key.rsplit("/", 1)[-1].removesuffix(".unity3d")
    tree["m_Name"] = logical_name
    tree["m_AssetBundleName"] = logical_name
    tree["m_Container"] = [
        [str(key).replace(old_leaf, leaf), value]
        for key, value in tree["m_Container"]
    ]
    bundle_obj.save_typetree(tree)

    std = dst_env.file.save(packer="lz4")
    (header_prefix, info_hash), _blocks, nodes, raw = octo.parse_unityfs(std)
    files = [(node[3], raw[node[0]:node[0] + node[1]]) for node in nodes]
    cab = next(name for name, _ in files if not name.endswith(".resS"))
    new_cab = "CAB-" + hashlib.sha256((logical_name + hashlib.sha256(image.tobytes()).hexdigest()).encode()).hexdigest()[:32]
    paths = [name.replace(cab, new_cab) for name, _ in files]
    payloads = [data.replace(cab.encode(), new_cab.encode()) for _, data in files]
    unityfs = octo.build_unityfs(header_prefix, info_hash, paths, payloads)
    output = octo.unityfs_to_octo(unityfs)

    # Read the produced Octo again, and compare complete Sprite geometry /
    # serialized visual fields against the Android source while allowing only
    # name and texture PPtr to differ.
    check = UnityPy.load(octo.octo_to_unityfs(output))
    check_file = serialized_file(check)
    if check_file.target_platform != 9 or check_file.unity_version != "2018.4.11f1":
        raise AssertionError("output lost the iOS target or Unity version")
    out_sprite = one(check, "Sprite").read()
    expected = sprite_signature(src_sprite)
    actual = sprite_signature(out_sprite)
    if expected != actual:
        differing = [key for key in expected if expected[key] != actual.get(key)]
        raise AssertionError(f"Sprite fields changed in round-trip: {differing}")
    out_tex = one(check, "Texture2D").read()
    out_tex_obj = one(check, "Texture2D")
    out_sprite_obj = one(check, "Sprite")
    if out_tex.image.size != image.size or out_tex.image.tobytes() != image.tobytes():
        raise AssertionError("pixel round-trip mismatch")
    if int(out_sprite.m_RD.texture.m_PathID) != int(out_tex_obj.path_id):
        raise AssertionError("Sprite texture pointer does not resolve to the bundle Texture2D")
    if not pointer_is_null(out_sprite.m_RD.alphaTexture):
        raise AssertionError("output Sprite unexpectedly depends on alphaTexture")
    bundle_tree = one(check, "AssetBundle").read_typetree()
    if bundle_tree["m_Name"] != logical_name or bundle_tree["m_AssetBundleName"] != logical_name:
        raise AssertionError("AssetBundle logical name mismatch")
    if len(bundle_tree["m_Container"]) != 2 or any(leaf not in key for key, _ in bundle_tree["m_Container"]):
        raise AssertionError("container entries were not renamed consistently")
    container_refs = {int(value["asset"]["m_PathID"]) for _, value in bundle_tree["m_Container"]}
    if container_refs != {int(out_tex_obj.path_id), int(out_sprite_obj.path_id)}:
        raise AssertionError("AssetBundle container does not resolve to its Sprite and Texture2D")
    if check_file.externals:
        raise AssertionError("output has external SerializedFile dependencies")
    report = {
        "logical_name": logical_name,
        "target_platform": check_file.target_platform,
        "unity_version": check_file.unity_version,
        "classes": sorted({obj.type.name for obj in check.objects}),
        "sprite_geometry_signature_sha256": hashlib.sha256(json.dumps(expected, sort_keys=True).encode()).hexdigest(),
        "vertices": int(out_sprite.m_RD.m_VertexData.m_VertexCount),
        "indices_bytes": len(out_sprite.m_RD.m_IndexBuffer),
        "texture": {"size": list(out_tex.image.size), "mode": out_tex.image.mode,
                    "rgba_sha256": hashlib.sha256(out_tex.image.tobytes()).hexdigest()},
        "externals": len(check_file.externals),
        "container_entries": len(bundle_tree["m_Container"]),
        "octo_bytes": len(output),
        "octo_sha256": hashlib.sha256(output).hexdigest(),
    }
    return output, report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="Android source Octo bundle")
    parser.add_argument("--donor", type=Path, required=True, help="verified iOS target_platform=9 donor Octo")
    parser.add_argument("--logical-name", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    data, report = make_bundle(args.source.read_bytes(), args.donor.read_bytes(), args.logical_name)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(data)
    report["output"] = str(args.output)
    print(json.dumps(report, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
