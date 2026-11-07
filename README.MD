### KSCSwitcher Extended

Forked from [KSCSwitcher](https://github.com/KSP-RO/KSCSwitcher/)

Switches your KSC, this time with multi-planetary support.

How to add a site: in a KSCSWITCHER Site node add   body = <CelestialBody name>
(see KSCSwitcher-Moon-Sites.cfg). Altitude (repositionRadiusOffset) is relative to the
body's radius.

Do note: Sites without 'body' behave exactly as before

What it does when an off-world site is picked (tracking station map icon or list):
 - re-parents the KSC to that body's terrain (PQS) and re-registers it there
 - points the stock Space Center scene / editor / tracking-station setup at that body
 - moves the launch pad / runway launch facilities and the KSC ground station with it
 - makes Kopernicus' KSC camera and floating-origin code look the KSC up on that body
 - draws each site's map icon on its own body; the magnifier focuses that body
Picking an Earth site brings the KSC home first, then KSCSwitcher places it as usual.

Experimental. If something breaks, KSP.log lines start with [KSCSwitcher-Moon].
Source: Source/KSCSwitcherMoon.cs
