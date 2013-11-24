RemoteTech2
===========

RemoteTech allows you to construct vast relay networks of communication satellites and remotely controlled unmanned vehicles.

Your unmanned vessels require an uplink to a command station to be controlled. Mission Control at the Kerbal Space Center delivers a vast omnidirectional antenna that will reach your vessels up to and slightly beyond Minmus.

### How to use

How to use Your unmanned vehicle requires two major components: a Signal Processor, and an antenna. These two must be used to form a connection.

### Signal Processors

Signal Processors come in three flavors: passive, normal, and command. All stock probes now feature a signal processor.

- Normal signal processors allow a vehicle to be part of a satellite network and be controllable as long as an uplink to any control station is maintained.
- Passive signal processors are included on every antenna and allow them to interact with the network during science transmissions, however they do not provide control.
- Command signal processors, currently only provided on the large probe stack by default, it allows a vehicle consisting of 6 or more kerbals to act as their very own Mission Control. While science transmissions need to route back to the Kerbal Space Center at all times, these do allow you to give control to any vehicle connected to them.

### Antennae

Antennae come in two flavors: omnidirectionals and dishes.

- Omnidirectional antennas form a connection with every vehicle in its spherical range but as a result have far lower range than a dish would have.

- Dishes form a long distance connection between two nodes, and both dishes are required to point at each other. Physically pointing them is not required, you just have to set the target vehicle on the dish. It is possible to form a dish connection with an omnidirectional antenna.

A secondary functionality in these dishes is the ability to target planets. When targeting a planet, the dish will look for a target anywhere in a cone towards that planet. How wide this cone can be is a property of the dish.

### Connections

The requirement to form a connection between any two vehicles is that both vehicles have an antenna that can reach the other. If the distance is 10km, both vehicles require an antenna with a minimum of 10km range. Your unmanned vehicle will become controllable as long as there is an active route between the vehicle and any command station.

### Signal Delay

Flight Computer (and/or kOS) controls will have an execution delay dependent on the distance and the speed of light. Managing your signal delay as well as pre-planning your actions becomes more important the further out you go. Signal Delay can be up to 15 minutes near Jool(!)

### Career Mode

All included parts have been integrated in the stock technology tree. Have fun! As an extra, once you unlock Unmanned Technology, all probes will feature an integrated 3km omnidirectional antenna at no cost.
