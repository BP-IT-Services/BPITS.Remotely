import * as UI from "./UI.js";
import { ViewerApp } from "./App.js";
import { CursorInfo } from "./Models/CursorInfo.js";
import { RemoteControlMode } from "./Enums/RemoteControlMode.js";
import { ShowToast } from "./UI.js";
import { WindowsSession } from "./Models/WindowsSession.js";
import { DtoType } from "./Enums/DtoType.js";
import { HubConnection } from "./Models/HubConnection.js";
import { ChunkDto } from "./DtoChunker.js";
import { MessagePack } from "./Interfaces/MessagePack.js";
import { ProcessStream } from "./CaptureProcessor.js";
import { HubConnectionState } from "./Enums/HubConnectionState.js";
import { StreamingState } from "./Models/StreamingState.js";
import { Result } from "./Models/Result.js";
import { RemoteControlViewerOptions } from "./Interfaces/Dtos.js";
import { GetSettings, SetSettings } from "./SettingsService.js";

const MsgPack: MessagePack = window["MessagePack"];

var signalR = window["signalR"];

export class ViewerHubConnection {
    Connection: HubConnection;
    PartialCaptureFrames: Uint8Array[] = [];
    private ElevationPublicKey: CryptoKey | null = null;


    Connect() {
        if (this.Connection) {
            this.Connection.stop();
        }

        this.Connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/viewer")
            .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
            .configureLogging(signalR.LogLevel.Information)
            .build();

        this.ApplyMessageHandlers(this.Connection);

        this.Connection.start().then(async () => {
            this.SendScreenCastRequestToDevice();
        }).catch(err => {
            console.error(err.toString());
            console.log("Connection closed.");
            UI.StatusMessage.innerHTML = `Connection error: ${err.message}`;
            UI.ToggleConnectUI(true);
        });

        this.Connection.onclose(() => {
            if (!UI.StatusMessage.innerText) {
                UI.SetStatusMessage("Connection closed.");
            }
            UI.ToggleConnectUI(true);
        });

        ViewerApp.ClipboardWatcher.WatchClipboard();
    }

    async ChangeWindowsSession(sessionID: number) {
        if (ViewerApp.Mode == RemoteControlMode.Unattended) {
            await this.Connection.invoke("ChangeWindowsSession", sessionID);
        }
    }

    async GetRemoteControlViewerOptions(): Promise<RemoteControlViewerOptions> {
        const settings = GetSettings();

        try {
            settings.ViewerOptions = await this.Connection.invoke<RemoteControlViewerOptions>("GetViewerOptions");
            SetSettings(settings);
            return settings.ViewerOptions;
        }
        catch (e) {
            console.error("Error while getting viewer options from server.", e);
            return settings.ViewerOptions;
        }
    }

    async InvokeCtrlAltDel() {
        if (this.Connection?.state != HubConnectionState.Connected) {
            return;
        }

        await this.Connection.invoke("InvokeCtrlAltDel");
    }

    async RequestElevation(username: string, domain: string, password: string) {
        if (this.Connection?.state != HubConnectionState.Connected || !this.ElevationPublicKey) {
            return;
        }

        const credJson = JSON.stringify({ Username: username, Domain: domain, Password: password });
        const encrypted = await crypto.subtle.encrypt(
            { name: "RSA-OAEP" },
            this.ElevationPublicKey,
            new TextEncoder().encode(credJson)
        );
        await this.Connection.invoke("RequestElevation", new Uint8Array(encrypted));
    }

    async SendDtoToClient<T>(dto: T, type: DtoType): Promise<void> {

        if (this.Connection?.state != HubConnectionState.Connected) {
            return;
        }

        let chunks = ChunkDto(dto, type);

        for (var i = 0; i < chunks.length; i++) {
            const chunk = MsgPack.encode(chunks[i]);
            await this.Connection.invoke("SendDtoToClient", chunk);
        }
    }

    // Subject is an interface that comes from the SignalR library.
    // But we can't use the TypeScript library like we would in
    // React/Vue, so we have to use "any" here.  This won't be an
    // issue when we rewrite the front-end.
    async SendRecordingChunks(subject: any) {
        await this.Connection.send("StoreSessionRecording", subject);
    }

    async SendScreenCastRequestToDevice() {
        const viewerOptions = await this.GetRemoteControlViewerOptions();

        const result = await this.Connection.invoke<Result>(
            "SendScreenCastRequestToDevice",
            ViewerApp.SessionId,
            ViewerApp.AccessKey,
            ViewerApp.RequesterName);

        if (!result.IsSuccess) {
            this.Connection.stop();
            UI.SetStatusMessage(result.Reason);
            return;
        }

        const streamingState = new StreamingState();
        ProcessStream(streamingState);

        if (viewerOptions.ShouldRecordSession) {
            ViewerApp.SessionRecorder.Start();
        }

        this.Connection.stream("GetDesktopStream")
            .subscribe({
                next: (chunk: Uint8Array) => {
                    streamingState.ReceivedChunks.push(chunk);
                },
                complete: () => {
                    streamingState.StreamEnded = true;
                    if (!UI.StatusMessage.innerText) {
                        UI.SetStatusMessage("Stream ended.");
                    }
                    ViewerApp.SessionRecorder.Stop();
                    UI.ToggleConnectUI(true);
                },
                error: (err) => {
                    console.warn(err);
                    streamingState.StreamEnded = true;
                    if (!UI.StatusMessage.innerText) {
                        UI.SetStatusMessage("Stream ended.");
                    }
                    ViewerApp.SessionRecorder.Stop();
                    UI.ToggleConnectUI(true);
                },
            });

    }


    private ApplyMessageHandlers(hubConnection) {
        hubConnection.on("SendDtoToViewer", async (dto: ArrayBuffer) => {
            await ViewerApp.DtoMessageHandler.ParseBinaryMessage(dto);
        });

        hubConnection.on("ConnectionFailed", () => {
            UI.ConnectButton.removeAttribute("disabled");
            UI.ConnectButton.innerText = "Connect";
            UI.SetStatusMessage("Connection failed or was denied.");
            ShowToast("Connection failed.  Please reconnect.");
            this.Connection.stop();
        });
        hubConnection.on("ReconnectFailed", () => {
            UI.ConnectButton.removeAttribute("disabled");
            UI.ConnectButton.innerText = "Connect";
            UI.SetStatusMessage("Unable to reconnect.");
            ShowToast("Unable to reconnect.");
            this.Connection.stop();
        });
        hubConnection.on("ConnectionRequestDenied", () => {
            UI.ConnectButton.innerText = "Connect";
            this.Connection.stop();
            UI.SetStatusMessage("Connection request denied.");
            ShowToast("Connection request denied.");
        });
        hubConnection.on("ViewerRemoved", () => {
            UI.ConnectButton.removeAttribute("disabled");
            UI.ConnectButton.innerText = "Connect";
            UI.SetStatusMessage("The session was stopped by your partner.");
            ShowToast("Session ended");
            this.Connection.stop();
        });
        hubConnection.on("ScreenCasterDisconnected", () => {
            UI.SetStatusMessage("The host has disconnected.");
            this.Connection.stop();
        });
        hubConnection.on("RelaunchedScreenCasterReady", (newSessionId: string, newAccessKey: string) => {
            const newUrl =
                `${location.origin}${location.pathname}` +
                `?mode=Unattended&sessionId=${newSessionId}&accessKey=${newAccessKey}&viewOnly=${ViewerApp.ViewOnlyMode}`;
            window.history.pushState(null, "", newUrl);
            ViewerApp.SessionId = newSessionId;
            ViewerApp.AccessKey = newAccessKey;
            this.SendScreenCastRequestToDevice();
        });

        hubConnection.on("Reconnecting", () => {
            UI.SetStatusMessage("Reconnecting");
            ShowToast("Reconnecting");
        });

        hubConnection.on("CursorChange", (cursor: CursorInfo) => {
            UI.UpdateCursor(cursor.ImageBytes, cursor.HotSpot.X, cursor.HotSpot.Y, cursor.CssOverride);
        });

        hubConnection.on("ReceiveElevationStatus", async (isElevated: boolean, publicKeyBytes: Uint8Array) => {
            if (publicKeyBytes && publicKeyBytes.length > 0) {
                this.ElevationPublicKey = await crypto.subtle.importKey(
                    "spki",
                    publicKeyBytes,
                    { name: "RSA-OAEP", hash: "SHA-256" },
                    false,
                    ["encrypt"]
                );
            } else {
                this.ElevationPublicKey = null;
            }
            UI.SetElevationStatus(isElevated);
        });

        hubConnection.on("ElevationFailed", (reason: string) => {
            ShowToast(`Elevation failed: ${reason}`);
        });

        hubConnection.on("ShowMessage", (message: string) => {
            ShowToast(message);
            UI.SetStatusMessage(message);
        });
        hubConnection.on("WindowsSessions", (windowsSessions: Array<WindowsSession>) => {
            UI.UpdateWindowsSessions(windowsSessions);
        });
        hubConnection.on("PingViewer", () => "Pong");
    }
}
