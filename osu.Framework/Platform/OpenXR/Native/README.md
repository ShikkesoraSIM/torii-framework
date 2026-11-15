# OpenXR native bindings

Minimal, hand-curated OpenXR bindings used by the immersive VR (Quest) host.

- `XrNative.g.cs` is **generated** by `generate.py` from the official Khronos headers. Don't edit it by hand.
- `bindings.txt` lists exactly which enums, flag groups, structs and functions are generated. To use more of the API, add
  entries there and regenerate.
- `XrFunctions.cs` / `XrLoaderLibrary.cs` / `XrResultExtensions.cs` are the hand-written glue: loading the loader,
  resolving entry points through `xrGetInstanceProcAddr`, and result checking.

## Why not Silk.NET.OpenXR?

- It looks for `libopenxr_loader.so.1` (the desktop Linux name) instead of Android's `libopenxr_loader.so`.
- `Silk.NET.Core` depends on `Microsoft.DotNet.PlatformAbstractions`, which osu! already has to pin around because of Realm.
- Function pointers (`delegate* unmanaged`) keep the bindings trimming-safe, allocation-free and dependency-free.

## Regenerating

```sh
# Use the same release as the Android loader the Quest build ships (org.khronos.openxr:openxr_loader_for_android).
V=1.1.65
mkdir -p /tmp/openxr && for f in openxr.h openxr_platform.h openxr_platform_defines.h; do
  curl -sfL -o /tmp/openxr/$f "https://raw.githubusercontent.com/KhronosGroup/OpenXR-SDK/release-$V/include/openxr/$f"
done
python3 generate.py /tmp/openxr
```

After regenerating, verify struct layouts against a C compiler: compile a program that prints `sizeof()` for every struct
in `bindings.txt` using the same headers, and compare with `Unsafe.SizeOf<T>()` for the generated types. All 71 structs
matched for 1.1.65 on x86-64 (same LP64 layout as arm64).
