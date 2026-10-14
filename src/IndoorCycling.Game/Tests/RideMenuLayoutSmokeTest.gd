extends SceneTree

const MENU_SCENE = preload("res://Tests/RideMenuLayoutSmokeScene.tscn")
var failures: Array[String] = []

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var host := SubViewportContainer.new()
	var viewport := SubViewport.new()
	viewport.size = Vector2i(960, 540)
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	host.size = viewport.size
	host.add_child(viewport)
	root.add_child(host)
	var menu = MENU_SCENE.instantiate()
	viewport.add_child(menu)
	await _settle()
	var panel: PanelContainer = menu.get_node("PanelContainer")
	var controller = menu
	var backdrop: ColorRect = menu.get_node("ColorRect")
	_check(backdrop.mouse_filter == Control.MOUSE_FILTER_STOP, "modal backdrop must block ride input")

	for viewport_size in [Vector2i(960, 540), Vector2i(1152, 648), Vector2i(1920, 1080)]:
		host.size = viewport_size
		viewport.size = viewport_size
		controller.ShowMainMenuForLayoutTest()
		await _settle()
		_check_panel(panel, viewport_size, "menu")
		var content: VBoxContainer = menu.get_node("PanelContainer/MarginContainer/MenuScroll/VBoxContainer")
		var title: Label = content.get_child(0)
		_check(abs(title.get_global_rect().get_center().x - panel.get_global_rect().get_center().x) <= 2.0,
			"menu heading must be centered in the panel at %s" % viewport_size)
		var first: Button = content.get_child(1)
		var last: Button = content.get_child(content.get_child_count() - 1)
		_check(abs(first.size.x - last.size.x) <= 1.0, "menu actions must have consistent widths")
		_check(abs(first.get_global_rect().position.x - (panel.get_global_rect().position.x + (panel.size.x - first.size.x) / 2.0)) <= 2.0,
			"menu action column must be balanced in the panel")
		if viewport_size == Vector2i(960, 540):
			_capture(viewport, "res://Tests/ride-menu-layout-menu.png")
		await process_frame
		_check(menu.get_viewport().gui_get_focus_owner() == first, "menu must focus its first action")

		for cycle in range(3):
			controller.ShowEndRideConfirmationForLayoutTest()
			await _settle()
			_check_panel(panel, viewport_size, "confirmation")
			_check(panel.size.y < min(300.0, viewport_size.y * 0.6), "confirmation retained excess menu height")
			var confirm_content: VBoxContainer = menu.get_node("PanelContainer/MarginContainer/MenuScroll/VBoxContainer")
			var explanation: Label = confirm_content.get_child(1)
			var actions: HBoxContainer = confirm_content.get_child(2)
			_check(explanation.text.contains("will not create a FIT file"), "confirmation must reflect disabled FIT recording")
			_check(abs(actions.get_child(0).size.x - actions.get_child(1).size.x) <= 1.0,
				"confirmation actions must have consistent widths")
			_check(panel.get_global_rect().end.y - actions.get_global_rect().end.y <= 52.0,
				"confirmation has excess empty area beneath actions")
			if cycle == 0 and viewport_size == Vector2i(960, 540):
				_capture(viewport, "res://Tests/ride-menu-layout-confirmation.png")
			var cancel: Button = actions.get_child(0)
			await process_frame
			_check(menu.get_viewport().gui_get_focus_owner() == cancel, "confirmation must focus Continue Riding")
			cancel.emit_signal("pressed")
			await _settle()
			_check_panel(panel, viewport_size, "menu after cancel")

	# Resize while each view is open so the deferred sizing path is exercised too.
	controller.ShowEndRideConfirmationForLayoutTest()
	await _settle()
	host.size = Vector2i(960, 540)
	viewport.size = Vector2i(960, 540)
	await _settle()
	_check_panel(panel, Vector2i(960, 540), "confirmation after resize")
	controller.ShowMainMenuForLayoutTest()
	await _settle()
	host.size = Vector2i(1920, 1080)
	viewport.size = Vector2i(1920, 1080)
	await _settle()
	_check_panel(panel, Vector2i(1920, 1080), "menu after resize")

	DirAccess.remove_absolute(ProjectSettings.globalize_path("res://Tests/ride-menu-layout-menu.png"))
	DirAccess.remove_absolute(ProjectSettings.globalize_path("res://Tests/ride-menu-layout-confirmation.png"))
	if failures.is_empty():
		print("[RideMenuLayoutSmoke] PASS: settled Godot menu/confirmation bounds, centering, focus, cancellation and resize.")
		quit(0)
	else:
		for failure in failures:
			push_error("[RideMenuLayoutSmoke] " + failure)
		quit(1)

func _settle() -> void:
	for frame in range(5):
		await process_frame

func _check_panel(panel: PanelContainer, viewport_size: Vector2i, view_name: String) -> void:
	var rect = panel.get_global_rect()
	_check(rect.position.x >= 10.0 and rect.position.y >= 10.0, "%s escaped viewport origin: %s" % [view_name, rect])
	_check(rect.end.x <= viewport_size.x - 10.0 and rect.end.y <= viewport_size.y - 10.0,
		"%s escaped viewport bounds %s: %s" % [view_name, viewport_size, rect])
	_check(abs(rect.get_center().x - viewport_size.x / 2.0) <= 2.0 and abs(rect.get_center().y - viewport_size.y / 2.0) <= 2.0,
		"%s must be centered in viewport %s: %s" % [view_name, viewport_size, rect])

func _check(condition: bool, message: String) -> void:
	if not condition:
		failures.append(message)

func _capture(viewport: SubViewport, path: String) -> void:
	RenderingServer.force_draw()
	var image := viewport.get_texture().get_image()
	if image.is_empty():
		failures.append("could not read rendered modal image at %s" % path)
		return
	var error := image.save_png(ProjectSettings.globalize_path(path))
	_check(error == OK, "could not save rendered modal image at %s" % path)
