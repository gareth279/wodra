extends SceneTree

const RIDE_SCENE = preload("res://Scenes/RideScreen.tscn")
var failures: Array[String] = []

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	DisplayServer.window_set_size(Vector2i(1138, 675))
	var ride = RIDE_SCENE.instantiate()
	root.add_child(ride)
	await _settle()
	ride.get_node("StartPanel").hide()
	for modal in root.find_children("*", "Window", true, false):
		modal.hide()
	var panel: Control = ride.get_node("DeviceConnectionPanel")
	panel.show()
	var display_panel: PanelContainer = panel.get_node("PanelContainer")
	var details: VBoxContainer = panel.get_node("PanelContainer/MarginContainer/VBoxContainer/ConnectionDetailsScroll/ConnectionDetails")
	var grid: GridContainer = details.get_node("GridContainer")
	var trainer_name: Label = grid.get_node("PowerCard/MarginContainer/VBoxContainer/DeviceLabel")
	var trainer_status: Label = grid.get_node("PowerCard/MarginContainer/VBoxContainer/StatusLabel")
	var hr_status: Label = grid.get_node("HeartRateCard/MarginContainer/VBoxContainer/StatusLabel")
	var steering_status: Label = grid.get_node("SteeringCard/MarginContainer/VBoxContainer/StatusLabel")
	var controller_status: Label = steering_status
	var controller_name: Label = grid.get_node("SteeringCard/MarginContainer/VBoxContainer/DeviceLabel")
	var controller_action: Button = grid.get_node("SteeringCard/MarginContainer/VBoxContainer/ActionButton")
	var controller_centre: Button = grid.get_node("SteeringCard/MarginContainer/VBoxContainer/SteeringCenterButton")
	var controller_type: OptionButton = grid.get_node("SteeringCard/MarginContainer/VBoxContainer/ControllerDeviceType")
	var overall: Label = details.get_node("OverallStatusLabel")
	var picker: OptionButton = grid.get_node("PowerCard/MarginContainer/VBoxContainer/TrainerPicker")
	var scroll: ScrollContainer = panel.get_node("PanelContainer/MarginContainer/VBoxContainer/ConnectionDetailsScroll")
	var footer: HBoxContainer = panel.get_node("PanelContainer/MarginContainer/VBoxContainer/ActionsRow")
	_check(controller_type.item_count == 2, "controller dropdown exposes only the supported auto and Sterzo choices")
	_check(controller_type.get_item_text(0) == "Auto-detect supported devices" and
		controller_type.get_item_text(1) == "Elite Sterzo Smart", "controller choice labels are clear and supported")
	_check(controller_action.text == "Connect" and not controller_centre.visible,
		"disconnected controller exposes Connect and hides Centre")
	for preference_index in [1, 0]:
		controller_type.select(preference_index)
		controller_type.emit_signal("item_selected", preference_index)
		await process_frame
		var saved_settings: Variant = JSON.parse_string(FileAccess.get_file_as_string(OS.get_user_data_dir() + "/settings/settings.json"))
		_check(saved_settings is Dictionary and saved_settings.get("ControllerDeviceType", -1) == preference_index,
			"controller device choice %s is persisted in application settings" % preference_index)

	for viewport_size in [Vector2i(1138, 675), DisplayServer.screen_get_size()]:
		if viewport_size.x <= 0 or viewport_size.y <= 0:
			continue
		if viewport_size == DisplayServer.screen_get_size():
			DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_FULLSCREEN)
		else:
			DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
			DisplayServer.window_set_size(viewport_size)
		await _settle()
		var actual_size: Vector2 = panel.get_viewport_rect().size
		var display_size = DisplayServer.window_get_size()
		var panel_rect: Rect2 = display_panel.get_global_rect()
		print("[DeviceConnectionLayoutSmoke] display=", display_size, " viewport=", actual_size,
			" ride=", ride.size, " panel=", panel_rect, " columns=", grid.columns)
		_check(panel_rect.position.x >= 0 and panel_rect.position.y >= 0,
			"panel remains inside the viewport at %s" % actual_size)
		_check(panel_rect.end.x <= actual_size.x and panel_rect.end.y <= actual_size.y,
			"panel fits viewport at %s" % actual_size)
		_check(abs(panel_rect.get_center().x - actual_size.x / 2.0) <= 2.0 and
			abs(panel_rect.get_center().y - actual_size.y / 2.0) <= 12.0,
			"panel remains centered at %s" % actual_size)
		_check(grid.columns == 3, "trainer, heart-rate, and steering cards share one row at %s" % actual_size)
		var cards = [grid.get_node("PowerCard"), grid.get_node("HeartRateCard"), grid.get_node("SteeringCard")]
		var widths: Array[float] = []
		for card in cards:
			var rect: Rect2 = card.get_global_rect()
			widths.append(rect.size.x)
			_check(rect.size.x >= 250, "device card keeps a readable minimum width at %s" % viewport_size)
		_check(abs(widths.max() - widths.min()) <= 2, "device cards have equal widths at %s" % viewport_size)
		_check(scroll.horizontal_scroll_mode == ScrollContainer.SCROLL_MODE_DISABLED, "connection details do not scroll horizontally")
		_check(footer.get_global_rect().end.y <= panel.get_global_rect().end.y - 8, "Back and Continue footer stays inside the panel")

	# Render the real connection cards in disconnected, scanning, ready, recovery,
	# multi-device and long-name states without starting a radio scan.
	trainer_name.text = "No trainer connected"
	trainer_status.text = "Not connected"
	overall.text = "Connect your trainer to begin."
	controller_name.text = "Elite Sterzo Smart"
	controller_status.text = "Left · −8.5°"
	controller_action.text = "Disconnect"
	controller_centre.show()
	_check(controller_status.text.contains("°") and controller_action.text == "Disconnect" and controller_centre.visible,
		"live controller state presents angle, Centre and Disconnect")
	await _capture("device-connection-controller-live.png")
	controller_status.text = "Disconnected · optional"
	controller_action.text = "Connect"
	controller_centre.hide()
	_check_state(grid, overall, hr_status, steering_status, "Connect your trainer to begin.", "Disconnected")
	await _capture("device-connection-disconnected.png")
	DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
	DisplayServer.window_set_size(Vector2i(1138, 675))
	await _settle()
	overall.text = "Searching nearby trainers · 00:12 elapsed"
	trainer_status.text = "Scanning · Auto"
	_check_state(grid, overall, hr_status, steering_status, "Searching nearby trainers · 00:12 elapsed", "Scanning")
	await _capture("device-connection-scanning.png")
	trainer_name.text = "Tacx Flux 2"
	trainer_status.text = "Bluetooth FTMS · 168 W · 82 rpm"
	overall.text = "Trainer ready"
	_check_state(grid, overall, hr_status, steering_status, "Trainer ready", "Connected")
	await _capture("device-connection-connected.png")
	trainer_status.text = "Connection lost · reconnecting selected trainer"
	overall.text = "Trainer connection lost. Recovering…"
	_check_state(grid, overall, hr_status, steering_status, "Trainer connection lost. Recovering…", "Recovery")
	await _capture("device-connection-recovery.png")
	picker.clear()
	picker.add_item("Flux 2 · remembered · 4206 / 17 / 5")
	picker.add_item("Other FE-C · 100 / 17 / 5")
	picker.select(0)
	picker.show()
	trainer_name.text = "Choose your trainer"
	trainer_status.text = "2 trainers found"
	await _capture("device-connection-multiple.png")
	picker.hide()
	trainer_name.text = "Tacx Flux 2 trainer with a long saved rider label"
	trainer_status.text = "ANT+ FE-C · connected · fresh power and cadence"
	await _capture("device-connection-long-name.png")

	# Exercise the actual pre-ride caller path and the active-ride menu route without
	# starting a ride, scanning a radio, or changing connection state.
	var menu = ride.get_node("RideMenuPanel")
	menu.ShowDeviceSettings()
	await _settle()
	await _capture("settings-pre-ride-devices.png")
	_check(menu.visible, "pre-ride device settings opens the shared settings overlay")
	var menu_content: VBoxContainer = menu.get_node("PanelContainer/MarginContainer/MenuScroll/VBoxContainer")
	var advanced = _button_with_text(menu_content, "Advanced ANT+ setup")
	_check(advanced != null, "Devices page offers Advanced ANT+ setup")
	if advanced != null:
		advanced.emit_signal("pressed")
		await _settle()
		await _capture("settings-pre-ride-ant-setup.png")
		_check(menu_content.get_child(0).text == "Devices · Advanced ANT+ setup", "advanced setup opens from Devices")
		var to_settings = _button_with_text(menu_content, "Back to Settings")
		if to_settings != null: to_settings.emit_signal("pressed")
		await _settle()
		await _capture("settings-pre-ride-settings.png")
		var back = _button_with_text(menu_content, "Back")
		if back != null: back.emit_signal("pressed")
		await _settle()
		await _capture("settings-return-to-connection.png")
		_check(not menu.visible and panel.visible, "pre-ride Settings Back returns to connection screen without Ride Menu")
		_check(root.gui_get_focus_owner() != null, "connection screen regains input focus after Settings closes")

	menu.ShowDeviceSettings()
	await _settle()
	var escape = InputEventKey.new()
	escape.keycode = KEY_ESCAPE
	escape.pressed = true
	Input.parse_input_event(escape)
	var escape_up = InputEventKey.new()
	escape_up.keycode = KEY_ESCAPE
	escape_up.pressed = false
	Input.parse_input_event(escape_up)
	await _settle()
	_check(menu.visible and menu_content.get_child(0).text == "Settings", "Escape from Devices follows its Back-to-Settings route")
	Input.parse_input_event(escape)
	Input.parse_input_event(escape_up)
	await _settle()
	_check(not menu.visible and panel.visible, "Escape from pre-ride Settings returns to the connection screen")

	menu.ShowMainSettingsForLayoutTest()
	await _settle()
	var ride_back = _button_with_text(menu_content, "Back")
	if ride_back != null: ride_back.emit_signal("pressed")
	await _settle()
	_check(menu.visible and menu_content.get_child(0).text == "Ride Menu" and
		_button_with_text(menu_content, "Continue Riding") != null,
		"active-ride Settings retains the Settings-to-Ride-Menu-to-Continue flow")
	var continue_riding = _button_with_text(menu_content, "Continue Riding")
	if continue_riding != null: continue_riding.emit_signal("pressed")
	await _settle()
	_check(not menu.visible and panel.visible, "Continue Riding closes the ride menu")

	if failures.is_empty():
		print("[DeviceConnectionLayoutSmoke] PASS: real RideScreen cards fit at minimum, target, and fullscreen sizes.")
		quit(0)
	else:
		for failure in failures:
			push_error("[DeviceConnectionLayoutSmoke] " + failure)
		quit(1)

func _settle() -> void:
	for frame in range(8):
		await process_frame

func _check_state(grid: GridContainer, overall: Label, hr_status: Label, steering_status: Label, expected: String, state: String) -> void:
	_check(overall.text == expected, "%s status is set" % state)
	_check(grid.get_node("HeartRateCard").visible and grid.get_node("SteeringCard").visible,
		"optional cards remain visible and independent in %s state" % state)
	if hr_status.text.strip_edges().is_empty() or steering_status.text.strip_edges().is_empty():
		failures.append("optional device statuses remain available in %s state" % state)

func _check(condition: bool, message: String) -> void:
	if not condition:
		failures.append(message)

func _button_with_text(root_node: Node, button_text: String) -> Button:
	for node in root_node.find_children("*", "Button", true, false):
		var button := node as Button
		if button != null and button.text == button_text:
			return button
	return null

func _capture(name: String) -> void:
	await process_frame
	await process_frame
	RenderingServer.force_draw()
	var image: Image = root.get_texture().get_image()
	if image == null or image.is_empty():
		failures.append("could not capture rendered %s state" % name)
		return
	image.save_png(ProjectSettings.globalize_path("res://.godot/" + name))
