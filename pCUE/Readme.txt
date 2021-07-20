DISCLAIMER:
USE THIS PROGRAM AT YOUR OWN RISK!
"Core Temp" IS A HARDWARE MONITORING TOOL, AND THERE IS A POSSIBILITY OF CAUSING CRASHES OR OTHER 
UNEXPECTED BEHAVIOR. THE AUTHOR OR DISTRIBUTOR OF "Core Temp" CAN NOT BE HELD RESPONSIBLE FOR ANY
DATA LOSS OR ANY OTHER DAMAGE WHICH MAY OCCUR DUE TO THE USE OF "Core Temp".
----------------------------------------------------------------------------------------------------------

Core Temp supports Intel and AMD processors.

Please read the FAQ before contacting me with questions, this way you save both of us time.
Link: http://www.alcpu.com/forums/viewtopic.php?f=63&t=892

Intel processors are supported starting with the Core series (Core Duo, Core Solo) and newer.
*** Pentium 4, Pentium D and derivatives like Xeon and Celeron processors are not supported ***

AMD processors are supported starting with the original Athlon 64 and newer.
Opteron processors are supported as well, excluding the very first Opteron 64 stepping.
----------------------------------------------------------------------------------------------------------

This a very straight forward program. Just run it and it will display the temperature of your processor's
Die temperature.
Here's how to use it. When you launch it the main window will appear, along with a system tray icon:

1) Hover the mouse over the icon with enumerate all cores and show their temperature.
2) Double-Left click will either show or hide the main window.
3) Minimizing the main window will minimize it to system tray.
4) Single-Right click will bring up the "File" menu.

There are also settings that you can adjust:

1) Set the interval between each temperature read (10 - 9999ms).
2) Set the interval between each write to the log file (Equal to read interval and up to 99999ms).
3) Toggle the logging On/Off.
4) Prevent from the "CPU is overheating!" message from appearing in case of overheating.
5) Show temperature in Fahrenheit - self explanatory.
6) Start minimzed - when checked will start Core Temp with the main window hidden.
7) Show Delta to Tj.Max. - Will display the output of the DTS value on Intel CPUs.
8) Start Core Temp with Windows - Check the checkbox to make Core Temp start together with Windows.

You can also adjust the settings for the system tray icons.

There is support for the Logitech G15 keyboard.
Core Temp will automatically launch a G15 applet, and display temperature on the G15 display.
It currently only supports single processor (up to 4 cores) systems.
Core Reacts to the Soft-Buttons:
Button1: Show current temperature.
Button2: Show high\low temperature per core.
Button3: Reserved, currently does nothing.
Button4: Closes the G15 applet (doesn't quit Core Temp, just disconnects it from the G15.
To get it back to the G15, rerun Core Temp).

Core Temp also features a temperature offset adjustment. If you feel or see that the temperatures are not
accurate on one or all cores you can adjust the values by adding or subtracting an offset.
You will find this feature in the Options menu.
----------------------------------------------------------------------------------------------------------

If you see (?) by the side of the temperature, it means that the value Core Temp is reporting could not
be validated, and may be incorrect.
(!) represents the history of the specific core/processor. If you see this sign, it means that your CPU
has reached it's maximum specified temperature at least once while Core Temp was running.
It indicates that you should check that your processor cooling is functioning properly.