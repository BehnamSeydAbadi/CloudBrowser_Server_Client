# Impossible unit tests (without production code changes)

These tests target **BrowserClient (UWP)**, **CEF live browser**, or **private production methods**. They cannot be implemented in `BrowserServer.Tests` without modifying the codebase.

## Bookmarks (client — BookmarkStore.cs)

- `BookmarkStore_Add_NormalizesUrl`
- `BookmarkStore_Add_UsesHostLabelWhenTitleEmpty`
- `BookmarkStore_Remove_RemovesExisting`
- `BookmarkStore_Toggle_AddsThenRemoves`
- `BookmarkStore_Contains_IsCaseInsensitive`
- `BookmarkStore_GetSnapshot_ReturnsCopy`
- `BookmarkStore_EnsureLoadedAsync_LoadsFromLocalFolder`
- `BookmarkStore_SaveSoonAsync_PersistsIndex`

## History (client store + private server navigation rules)

- `HistoryStore_NormalizeUrl_StripsDefaultPort`
- `HistoryStore_NormalizeUrl_RejectsAboutAndDataUrls`
- `HistoryStore_NormalizeUrl_AddsHttpWhenMissing`
- `HistoryStore_HostLabel_ReturnsDisplayHost`
- `HistoryStore_Record_MovesDuplicateToTop`
- `HistoryStore_Suggest_FiltersByPrefix`
- `HistoryStore_Remove_RemovesEntry`
- `HistoryStore_Clear_EmptiesStore`
- `Program_IsBlankNavigationUrl_TreatsAboutBlankAsBlank`
- `Program_SendAtHistoryRoot_WhenBackBlockedByBlankEntry`

## Video playback (CEF / OnPaint)

- `VideoPlaybackBridge_Poll_DetectsPlayingVideoElement`
- `VideoPlaybackBridge_Poll_ClearsPlayingWhenNoVideo`
- `VideoPlaybackBridge_HandlePaint_EncodesJpegForStreamingTab`
- `VideoPlaybackBridge_IsStreaming_TrueWhileTabPlaying`
- `VideoPlaybackBridge_SkipsPaintWhileDownloadStreaming`

## Notifications (client native toast)

- `NativeNotification_Show_BuildsValidToastXml`
- `NativeNotification_Show_TruncatesLongTitleAndBody`
- `NativeNotification_Show_EscapesXmlSpecialCharacters`
- `WebBrowserDataSource_RespondNotificationPermissionAsync_SendsGrantPacket`
- `WebBrowserDataSource_RespondNotificationPermissionAsync_SendsDenyPacket`
- `MainPage_HandleServerNotification_InvokesNativeNotification`
- `MainPage_ShowNotificationPermissionDialogAsync_ReturnsUserChoice`

## Camera + QR (client capture + private server internals)

- `ClientMediaCapture_BuildCamPacket_WrapsJpegWithMagic`
- `ClientMediaCapture_FrameToMicPacket_EncodesPcm`
- `ClientMediaCapture_RotateBgra_OrientsSensorBuffer`
- `MainPage_CameraPermission_AllowsServerCapture`
- `QrScanService_Cooldown_SuppressesDuplicateWithin3500ms`
- `QrScanService_NonHttpPayload_ShowsToastOnly`
- `QrScanService_HttpPayload_NavigatesActiveTab`
- `MediaBridge_PushJpegToPages_InjectsIntoPageScript`

## File downloads (private chunk codec + client reassembly)

- `StreamingDownloadHandler_BuildChunkPacket_EncodesSeqAndIsLast`
- `StreamingDownloadHandler_FlushOutbound_RespectsAckWindow`
- `StreamingDownloadHandler_StreamUrlToClient_StreamsRemoteUrl`
- `DownloadStore_TryParseFilePacket_ValidatesMagicAndLength`
- `DownloadStore_HandleFilePacketAsync_ReassemblesChunksInOrder`
- `DownloadStore_OnProgress_EmitsPercentSteps`
- `DownloadStore_TryGetFileAsync_OpensCompletedFile`
- `MainPage_DownloadOverlay_ReflectsStoreSnapshot`

## Tabs (CEF browser lifecycle)

- `TabManager_CreateTab_EnforcesMaxTabs`
- `TabManager_CreateTab_AssignsUniqueId`
- `TabManager_CloseTab_DisposesBrowser`
- `TabManager_SwitchTab_ActivatesTarget`
- `TabManager_RestoreFromSnapshot_OpensSavedUrls`
- `TabManager_SendTabList_IncludesAllOpenTabs`
- `ClientEnvironmentBridge_Apply_RestoresTabsFromSnapshot`

## Audio forwarding (CEF audio handler + client playback)

- `StreamingAudioHandler_OnAudioStreamPacket_BuildsAudiFrames`
- `StreamingAudioHandler_FlushOutbound_SendsToActiveSession`
- `StreamAudioPlayer_SubmitPacket_FeedsAudioGraph`
- `StreamAudioPlayer_Stop_ClearsPendingPcm`
- `WebBrowserDataSource_RoutesAudiBinaryToStreamAudioPlayer`
- `WebBrowserDataSource_AudioStop_ClearsPlayer`

## Auto-save last server address (client settings)

- `WebBrowserDataSource_StartRecive_PersistsLastServerUrl`
- `MainPage_Constructor_RestoresLastServerUrl`
- `MainPage_Connect_UsesSavedUrlWhenFieldEmpty`
- `MainPage_PinnedSecondaryTile_UsesLastServerUrl`

## Text input (client UI + CEF key injection)

- `MainPage_BeginPageTyping_ShowsInputPane`
- `MainPage_EndPageTyping_HidesInputPane`
- `MainPage_KeyboardCaptureBox_TextChanged_ForwardsInsertText`
- `Program_TextInputSend_ExecutesInsertTextInBrowser`
- `Program_SendKey_MapsVirtualKeyToCefKeyEvent`
- `BrowserHandlers_OnVirtualKeyboardRequested_SendsTextInputContent`

## Multitouch (client pointer math + CEF SendTouchEvent)

- `MainPage_GetNormalizedPointerPosition_MapsImageCoordinates`
- `MainPage_PointerMovedEnough_RequiresMinimumDelta`
- `Program_Touch_MapsNormalizedToPixelCoordinates`
- `Program_Touch_SendsCefTouchEventWithCorrectType`
- `WebBrowserDataSource_TouchDown_SendsOverOpenWebSocket`

## Auto scaling render view (client display + CEF Size)

- `MainPage_BuildClientEnvironment_IncludesCssSizeAndDpr`
- `MainPage_OrientationChanged_RecomputesScaleRect`
- `WebBrowserDataSource_SizeChange_SendsWidthHeightScale`
- `TabManager_ApplyPendingViewport_SetsBrowserSizeAndDpr`
- `RenderFrameSession_ScalesJpegToClientViewport`
