# tools/hd_heroes

* `refit.py` – puts a new character mesh (generated or hand-made) on an original ParaWorld skeleton and writes it into
  `content/hd` for the option *HD heroes*.
* `preview_look.py` – renders a model in the original Lambert look and in the painterly look side by side.

Both run with Blender (`blender -b --python <tool> -- ...`) or the `bpy` module, and `refit.py` also needs scipy.
How they work: [docs/HD_HEROES.md](../../docs/HD_HEROES.md). The workflow for a new hero (references, prompts,
image-to-3D, `run_refit.bat`) is in `E:\Paraworld\HD_Heroes\README.md`.
