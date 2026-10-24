# Windows media-session controls

Wodra's optional in-ride media surface is a client of the operating system's
selected Global System Media Transport Controls session. It does not connect to
a particular player or account. The operating system chooses the current
session, and Wodra reads its title, artist, playback status, and advertised
capabilities.

`ISystemMediaSessionProvider` in Core is the platform boundary. The Windows
implementation lives in `WindowsMediaSessionProvider`; other platforms can
leave the feature unavailable or provide their own implementation without
changing ride simulation. It uses the Windows 10 1809 (build 17763) media
session API through the Windows SDK .NET projection. Windows packaging includes
that projection. Older Windows versions and API failures result in an absent
panel.

The preference is off by default and stored in the existing application
settings. When enabled, the ride HUD polls the OS-selected session once per
second. It clears metadata when no session is selected, a player closes, or a
read fails. Play/pause, next, and previous are shown only when the selected
session advertises the corresponding capability. Controls target the OS-selected
session at command time so a player switch takes effect without restarting the
ride.

All WinRT requests run away from the ride/render thread, are serialized, and
have a 900 ms caller timeout. A command failure or timeout is treated as an
unavailable result; it cannot block ride updates. No media keys are added to
the riding input map because its existing keys include steering and ride
actions. The compact panel buttons provide media commands without changing
those bindings.
