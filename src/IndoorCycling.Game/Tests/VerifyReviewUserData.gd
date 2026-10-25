extends SceneTree

func _initialize() -> void:
	var root = OS.get_environment("WODRA_REVIEW_ROOT")
	var actual = ProjectSettings.globalize_path("user://")
	var normalized_root = root.replace("\\", "/").trim_suffix("/").to_lower()
	var normalized_actual = actual.replace("\\", "/").trim_suffix("/").to_lower()
	if root.is_empty() or not normalized_actual.begins_with(normalized_root + "/"):
		printerr("ISOLATION PREFLIGHT FAILED: user:// resolves outside the disposable review root: ", actual)
		quit(41)
		return
	print("ISOLATION PREFLIGHT PASS: user:// = ", actual)
	quit(0)
