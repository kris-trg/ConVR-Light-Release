# ConVR-Light



This is the desktop version of the VR conversation training application ConVRsations (https://github.com/Raven0227/Beratungszimmer). Developed in Unity 6.3 (URP).

It simulates a counseling video call.



### Changes to ConVRsations (VR)



ConVRsations Light requires no VR headset.

The following changes were made to the original VR application:

\- stationary camera, no movement

\- added interface to resemble video call

\- changed some assets in the background/environment



### Setup



To use ConVRsations Light:

1\. Clone this Repo and open it in Unity

2\. Create an ApiConfig.asset file and add your API keys. 

&#x09;We use Azure and Claude, you'll have to adapt the scripts in the SCRIPTS/AI folder if you use different AI.

3\. Drag the file to all necessary fields in the inspector window (AIConversationManager.cs, AvatarSelectionUI.cs, AvatarSpawner.cs)

4\. Change reference link (MainMenu/Canvas/Panel_Config/Prompt Cloud URL) to wherever you store your prompts, or use the default: https://raw.githubusercontent.com/kris-trg/ConVR-Light-Release/main/Prompts/prompts.json

5\. If you do not have prompts yet, download the Prompt Editor zip-Folder from the main ConVRsations Repo (linked above; see Authoring > Prompt Editor.zip) to help you create some

6\. Create Build: File > Build Profiles > Windows > Build

7\. Run on your device









