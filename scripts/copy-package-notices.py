"""Copy dependency metadata and supplied notices into a package; Python standard library only."""
import json
import pathlib
import shutil
import sys

assets_path, destination = map(pathlib.Path, sys.argv[1:])
assets = json.loads(assets_path.read_text(encoding="utf-8-sig"))
for library in assets["libraries"].values():
    if library.get("type") != "package":
        continue
    for cache in assets["packageFolders"]:
        package = pathlib.Path(cache) / library["path"]
        if not package.is_dir():
            continue
        output = destination / library["path"].replace("/", "-")
        for source in package.rglob("*"):
            if not source.is_file() or source.suffix.lower() in {".dll", ".pdb", ".png"}:
                continue
            if source.suffix == ".nuspec" or any(word in source.name.lower() for word in ("license", "notice", "copying")):
                target = output / source.relative_to(package)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
