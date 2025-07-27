Quick Setup Instructions (Windows) (Skip 1&2 if you have Noxy-RED.core installed)

1. Install Mosquitto MQTT Broker (Local)

Steps:

    Download the installer from the official site:
    👉 https://mosquitto.org/download/

    Run the installer. During setup:

        Accept default options

        Ensure “Install Service” is checked

    After installation:

        Start Mosquitto using Services (services.msc)

        OR manually from Command Prompt:

    "C:\Program Files\mosquitto\mosquitto.exe"

(Optional) Test it's working:
Open two Command Prompts and run:

    Subscriber:

mosquitto_sub -t test

Publisher:

        mosquitto_pub -t test -m "hello"

2. Install Node-RED (Local)

Steps:

    Install Node.js (LTS version) from:
    👉 https://nodejs.org/

    Open Command Prompt as Administrator and run:

npm install -g --unsafe-perm node-red

Start Node-RED:

    node-red

    Open your browser to access the editor:
    👉 http://localhost:1880

3. Import Flow File: example_MFP.networked.json

    Start Node-RED and go to http://localhost:1880

    Click the menu (☰ top-right) → Import

    Choose "Upload" and select the file example_MFP.networked.json

    Click Import, then Deploy to activate the flow