# Recovered Unity assets: evidence and next steps

The sibling asset checkout contains recoverable Kick Flight Unity content, but it does not contain an original Unity project. The Octo snapshot has 2,374 files in `octo_sorted/3_unity_bundles/`; repairing the Octo wrapper in memory and loading every file with the existing UnityPy 1.25.4 succeeded for all 2,374. Each parsed bundle reports UnityFS format 6 and engine version `2018.4.11f1`. The legacy asset repository README documents the same Unity target. This identifies the serialized content version; it does not establish which current Unity editor can build an iOS client or whether this content can be loaded unchanged by a new project.

A five-bundle sample was selected from Unity `AssetBundle.m_Container` paths, then checked by object type and path ID rather than by opaque Octo filenames. The evidence in [assets-sample-evidence.json](assets-sample-evidence.json) records source and reconstructed bundle hashes, container references, type counts, selected object IDs, and hashes for the local converted views. It contains metadata only; no recovered binary asset is tracked.

| Role | Parsed evidence | What it establishes |
| --- | --- | --- |
| PC001 body | `player/pc_001/pc_001_001.unity3d`: Animator, Avatar, SkinnedMeshRenderer, Mesh, materials, textures, 82 GameObjects and 82 Transforms | A real character body asset is recoverable. The AssetBundle entry points to `pc_001_001.prefab`. |
| PC001 animation | `player/pc_001/animator/pc_001_101.unity3d`: one AnimatorController and 53 AnimationClips | Controller and clip objects are present. Their binding to a reconstructed character prefab has not been tested. |
| FLD00101 map | `field/fld00101.unity3d`: 105 GameObjects, 5 Meshes, 16 textures, and nodes named `PlayerPositions`, `TeamA`, `TeamB`, and `RouteManager` | A battle field and gameplay placement nodes are present in the recovered scene data. |
| FLD00101 field data | `field/fielddata/fld00101_1.unity3d`: MonoBehaviour `FLD00101_1` | A separate field-data object is available; its script behavior still needs reconstruction. |
| FLD00101 minimap | `ui/minimap/mim00101_0.unity3d`: Texture2D and Sprite | A minimap image and sprite object are present. |

## Reproduce the bounded inventory

Use the already available asset virtual environment. The tool reads source files, repairs wrappers in memory, and writes only a JSON inventory. A directory scan is capped at 25 bundles by default; choose an explicit cap for a larger scan. `--name-regex` reduces the objects retained in the report after parsing each bundle.

```bash
ASSETS=/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Assets
$ASSETS/.venv/bin/python scripts/ios/inventory_octo_assets.py \
  --assets-root "$ASSETS" \
  --input "$ASSETS/octo_sorted/3_unity_bundles" \
  --max-bundles 25 \
  --name-regex '(pc_001|FLD00101|mim00101)' \
  --output .local/ios-assets/inventory.json
```

For complete source-snapshot verification, the command used `--max-bundles 2374`. It completed with 2,374 successes and zero errors in approximately 40 seconds on the available host. Source file bytes are read only; JSON output can be directed under ignored `.local/` as shown.

## Reproduce the small PC001 export

The exporter requires exact object-name matching and has a hard maximum of 32 objects per call. It writes only UnityPy's PNG and Wavefront OBJ representations plus a hash/provenance manifest. It does not copy or rewrite the source bundle.

```bash
ASSETS=/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Assets
BUNDLE="$ASSETS/octo_sorted/3_unity_bundles/41613732347465_adae1f0320b9466e949548c5bb0e9412.bundle"
$ASSETS/.venv/bin/python scripts/ios/export_unity_sample.py \
  --assets-root "$ASSETS" \
  --bundle "$BUNDLE" \
  --output-dir .local/ios-assets/export-pc001-sample \
  --name-regex '^(Body|pc_tx_001_001|pc_tx_001_001_ma)$' \
  --max-objects 3
```

The run exported the `Body` mesh as OBJ (3,792 vertices and 4,100 faces), a 1024×1024 `pc_tx_001_001` texture, and a 512×512 `pc_tx_001_001_ma` texture. Pillow decoded both PNGs and the OBJ passed finite-vertex parsing. Output SHA-256 values and the input bundle SHA-256 are in the evidence JSON; the actual converted files and full manifests remain in ignored `.local/ios-assets/`.

## Existing tooling and limits

The sibling `Kick-Flight-Assets` repository already has `reconstruct_unity_bundles.py` for repairing Octo UnityFS wrappers while preserving CAB and `.resS` nodes, and `asset_extractor.py` for broader UnityPy conversion and media preservation. Its full pipeline also preserves AnimationClip serialized bytes and emits typetree views where UnityPy supports them. These are recovery and inspection tools, not a Unity project generator. A scan found no `ProjectVersion.txt`, `.unityproj`, or Xcode project in the asset checkout or this worktree; the README explicitly says the existing pipeline does not generate a ready-to-build Unity project.

A converted mesh and texture are useful input to a reconstruction, not proof that their Unity materials, shader variants, rig, animation bindings, prefab components, field scripts, or scene references have been restored. The data objects and MonoBehaviours may depend on original scripts and serialization details. No bulk asset export was performed. No Unity Editor/Hub was available on the inventory host, so no Unity import, iOS AssetBundle build, iOS player build, or runtime validation is claimed. Keep recovered binary exports out of Git; use the hash manifest to reproduce and compare them locally.
