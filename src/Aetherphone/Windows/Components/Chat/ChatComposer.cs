using Aetherphone.Core;
using Aetherphone.Core.Localization;
using Aetherphone.Core.Media;
using Aetherphone.Core.Notifications;
using Aetherphone.Core.Platform;
using Aetherphone.Core.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;

namespace Aetherphone.Windows.Components;

internal struct ChatComposerModel
{
    public AppSkin Ui;
    public string ConversationId;
    public int MaxLength;
    public bool Sending;
    public bool CanImage;
    public bool CanVoice;
    public bool CanLocation;
    public bool CanHandleEscape;
    public bool Blocked;
    public string BlockedNotice;
    public Action OnBlockedTap;
    public Func<int> ResolveVoiceInput;
    public Action<string> OnPickImage;
    public Action<string> OnShareLocation;
    public Action<string, string, string?> OnSendText;
    public Action<string, string, string> OnEditText;
    public Action<string, byte[], int> OnSendVoice;
    public Action<string, string> OnSendImage;
}

internal sealed class ChatComposer : IDisposable
{
    private const int TextKind = 0;
    private const float AccessoryBarHeight = 46f;
    private const float PastedBarHeight = 84f;
    private const int PasteMaxDimension = 2048;
    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 FieldFill = new(1f, 1f, 1f, 0.10f);
    private static readonly Vector4 BarFill = new(1f, 1f, 1f, 0.05f);

    private readonly VoiceNoteRecorder recorder = new();
    private readonly EmojiPicker emojiPicker = new();
    private string draft = string.Empty;
    private bool focus;
    private bool emojiOpen;
    private string? replyTargetId;
    private string replyBarName = string.Empty;
    private string replyBarPreview = string.Empty;
    private string? editTargetId;
    private string editBarPreview = string.Empty;
    private volatile bool pasteReadDone;
    private byte[]? pasteReadResult;
    private bool pasteBusy;
    private string? pastedImagePath;
    private volatile IDalamudTextureWrap? pastedTexture;
    private int pasteGeneration;

    public string Draft
    {
        get => draft;
        set => draft = value;
    }

    public bool IsEditing => editTargetId is not null;

    public bool HasReplyTarget => replyTargetId is not null;

    public bool Recording => recorder.Recording;

    private bool HasPastedImage => pastedImagePath is not null;

    public float AccessoryHeight => UiScale.Current * ((replyTargetId is not null || editTargetId is not null
        ? AccessoryBarHeight
        : 0f) + (HasPastedImage ? PastedBarHeight : 0f));

    public void BeginReply(string messageId, string senderName, string preview)
    {
        ClearEdit();
        replyTargetId = messageId;
        replyBarName = senderName;
        replyBarPreview = preview;
        focus = true;
    }

    public void BeginEdit(string messageId, string body)
    {
        ClearReply();
        editTargetId = messageId;
        editBarPreview = ChatText.QuotePreview(body, TextKind);
        draft = body;
        focus = true;
    }

    public void ClearReply()
    {
        replyTargetId = null;
        replyBarName = string.Empty;
        replyBarPreview = string.Empty;
    }

    public void ClearEdit()
    {
        if (editTargetId is null)
        {
            return;
        }

        editTargetId = null;
        draft = string.Empty;
    }

    public void ClearTargets()
    {
        replyTargetId = null;
        replyBarName = string.Empty;
        replyBarPreview = string.Empty;
        editTargetId = null;
        DiscardPastedImage();
    }

    public void Clear()
    {
        ClearTargets();
        draft = string.Empty;
    }

    public void CancelVoice()
    {
        recorder.Cancel();
    }

    public void Dispose()
    {
        DiscardPastedImage();
        recorder.Dispose();
    }

    public void Draw(Rect composerRect, in ChatComposerModel model)
    {
        PumpPendingPaste();
        var accessory = AccessoryHeight;
        if (accessory > 0f)
        {
            var top = composerRect.Min.Y - accessory;
            if (HasPastedImage)
            {
                var pastedHeight = PastedBarHeight * UiScale.Current;
                DrawPastedBar(new Rect(new Vector2(composerRect.Min.X, top),
                    new Vector2(composerRect.Max.X, top + pastedHeight)), model);
                top += pastedHeight;
            }

            if (replyTargetId is not null || editTargetId is not null)
            {
                var barRect = new Rect(new Vector2(composerRect.Min.X, top),
                    new Vector2(composerRect.Max.X, composerRect.Min.Y));
                if (editTargetId is not null)
                {
                    DrawEditBar(barRect, model);
                }
                else
                {
                    DrawReplyBar(barRect, model);
                }
            }
        }

        if (model.Blocked)
        {
            DrawBlockedComposer(composerRect, model);
            return;
        }

        if (recorder.Recording)
        {
            DrawRecordingComposer(composerRect, model);
            return;
        }

        DrawInputComposer(composerRect, model);
    }

    private static void DrawBlockedComposer(Rect area, in ChatComposerModel model)
    {
        var ui = model.Ui;
        var scale = UiScale.Current;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(area.Min, new Vector2(area.Max.X, area.Min.Y), ImGui.GetColorU32(ui.Theme.Separator), 1f);

        var edgePad = 14f * scale;
        var iconRadius = 13f * scale;
        var iconCenter = new Vector2(area.Min.X + edgePad + iconRadius, area.Center.Y);
        AppSkin.Icon(iconCenter, IconGlyph.Of(FontAwesomeIcon.Lock), ui.MutedInk, 0.9f);

        var textLeft = iconCenter.X + iconRadius + 9f * scale;
        var label = Typography.FitText(model.BlockedNotice, area.Max.X - edgePad - textLeft, TextStyles.Footnote);
        var labelSize = Typography.Measure(label, TextStyles.Footnote);
        Typography.Draw(drawList, new Vector2(textLeft, area.Center.Y - labelSize.Y * 0.5f), label, ui.MutedInk,
            TextStyles.Footnote);

        if (UiInteract.Hover(area.Min, area.Max))
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (UiInteract.HoverClick(area.Min, area.Max))
        {
            model.OnBlockedTap?.Invoke();
        }
    }

    private void DrawInputComposer(Rect area, in ChatComposerModel model)
    {
        var ui = model.Ui;
        var theme = ui.Theme;
        var scale = UiScale.Current;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(area.Min, new Vector2(area.Max.X, area.Min.Y), ImGui.GetColorU32(theme.Separator), 1f);
        var buttonRadius = 18f * scale;
        var iconRadius = 15f * scale;
        var edgePad = 10f * scale;
        var sendCenter = new Vector2(area.Max.X - edgePad - buttonRadius, area.Center.Y);
        var pillMin = new Vector2(area.Min.X + edgePad, area.Min.Y + 7f * scale);
        var pillMax = new Vector2(sendCenter.X - buttonRadius - 8f * scale, area.Max.Y - 7f * scale);
        Squircle.Fill(drawList, pillMin, pillMax, (pillMax.Y - pillMin.Y) * 0.5f, ImGui.GetColorU32(FieldFill));

        var emojiCenter = new Vector2(pillMin.X + iconRadius + 5f * scale, area.Center.Y);
        var emojiMin = emojiCenter - new Vector2(iconRadius, iconRadius);
        var emojiMax = emojiCenter + new Vector2(iconRadius, iconRadius);
        var emojiHovered = UiInteract.Hover(emojiMin, emojiMax);
        var emojiColor = emojiOpen ? ui.Accent : emojiHovered ? theme.TextStrong : ui.MutedInk;
        AppSkin.Icon(emojiCenter, IconGlyph.Of(FontAwesomeIcon.Smile), emojiColor, 0.95f);
        HoverTooltip.Show(new Rect(emojiMin, emojiMax), Loc.T(L.Common.Emoji), HoverLabelSide.Above);
        if (emojiHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                emojiOpen = !emojiOpen;
            }
        }

        var textRight = pillMax.X - 14f * scale;
        var trailingIconX = pillMax.X - iconRadius - 5f * scale;
        if (model.CanImage)
        {
            var pictureCenter = new Vector2(trailingIconX, area.Center.Y);
            var pictureMin = pictureCenter - new Vector2(iconRadius, iconRadius);
            var pictureMax = pictureCenter + new Vector2(iconRadius, iconRadius);
            var pictureHovered = UiInteract.Hover(pictureMin, pictureMax);
            AppSkin.Icon(pictureCenter, IconGlyph.Of(FontAwesomeIcon.Image),
                pictureHovered ? theme.TextStrong : ui.MutedInk, 0.95f);
            HoverTooltip.Show(new Rect(pictureMin, pictureMax), Loc.T(L.Velvet.SendPicture), HoverLabelSide.Above);
            if (pictureHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    model.OnPickImage(model.ConversationId);
                }
            }

            trailingIconX = pictureMin.X - iconRadius - 4f * scale;
            textRight = pictureMin.X - 6f * scale;
        }

        if (model.CanLocation)
        {
            var locationCenter = new Vector2(trailingIconX, area.Center.Y);
            var locationMin = locationCenter - new Vector2(iconRadius, iconRadius);
            var locationMax = locationCenter + new Vector2(iconRadius, iconRadius);
            var locationHovered = UiInteract.Hover(locationMin, locationMax);
            AppSkin.Icon(locationCenter, IconGlyph.Of(FontAwesomeIcon.MapMarkerAlt),
                locationHovered ? theme.TextStrong : ui.MutedInk, 0.95f);
            HoverTooltip.Show(new Rect(locationMin, locationMax), Loc.T(L.Message.ShareLocation),
                HoverLabelSide.Above);
            if (locationHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    model.OnShareLocation(model.ConversationId);
                }
            }

            textRight = locationMin.X - 6f * scale;
        }

        var textLeft = emojiMax.X + 4f * scale;
        ImGui.SetCursorScreenPos(new Vector2(textLeft,
            (pillMin.Y + pillMax.Y) * 0.5f - ImGui.GetFrameHeight() * 0.5f));
        ImGui.SetNextItemWidth(MathF.Max(1f, textRight - textLeft));
        if (focus)
        {
            ImGui.SetKeyboardFocusHere();
            focus = false;
        }

        var submitted = false;
        Plugin.Fonts.NoticeText(draft);
        using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f)))
        using (ImRaii.PushColor(ImGuiCol.Text, theme.TextStrong))
        {
            if (ImGui.InputTextWithHint("##chatComposerInput", Loc.T(L.Velvet.MessageHint), ref draft, model.MaxLength,
                    ImGuiInputTextFlags.EnterReturnsTrue))
            {
                submitted = true;
            }
        }

        if (ImGui.IsItemActive() && !IsEditing && !pasteBusy && !HasPastedImage
            && ImGui.IsKeyPressed(ImGuiKey.V)
            && (ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl))
            && ClipboardPaste.HasImage())
        {
            pasteBusy = true;
            _ = Task.Run(() =>
            {
                byte[]? result = null;
                try
                {
                    result = ClipboardPaste.ReadImagePng(PasteMaxDimension);
                }
                catch (Exception exception)
                {
                    AepLog.Warning(exception, "Clipboard image paste failed");
                }

                pasteReadResult = result;
                pasteReadDone = true;
            });
        }

        var hasDraft = draft.Trim().Length > 0;
        var hasPasted = HasPastedImage;
        var canSend = (hasDraft || hasPasted) && !model.Sending;
        var sendRect = new Rect(sendCenter - new Vector2(buttonRadius, buttonRadius),
            sendCenter + new Vector2(buttonRadius, buttonRadius));
        if (hasDraft || hasPasted)
        {
            drawList.AddCircleFilled(sendCenter, buttonRadius,
                ImGui.GetColorU32(canSend ? ui.Accent : theme.SurfaceMuted), 24);
            AppSkin.Icon(sendCenter, IconGlyph.Of(FontAwesomeIcon.PaperPlane), White, 0.9f);
            HoverTooltip.Show(sendRect, Loc.T(L.Velvet.Send), HoverLabelSide.Above);
            if (UiInteract.Hover(sendRect.Min, sendRect.Max))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && canSend)
                {
                    submitted = true;
                }
            }
        }
        else if (model.CanVoice)
        {
            drawList.AddCircleFilled(sendCenter, buttonRadius, ImGui.GetColorU32(ui.Accent), 24);
            AppSkin.Icon(sendCenter, IconGlyph.Of(FontAwesomeIcon.Microphone), White, 0.9f);
            HoverTooltip.Show(sendRect, Loc.T(L.Message.RecordVoiceHint), HoverLabelSide.Above);
            if (UiInteract.Hover(sendRect.Min, sendRect.Max))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !model.Sending)
                {
                    recorder.Start(model.ResolveVoiceInput());
                    UiFeedback.Play(UiSound.RecordStart);
                }
            }
        }
        else
        {
            drawList.AddCircleFilled(sendCenter, buttonRadius, ImGui.GetColorU32(theme.SurfaceMuted), 24);
            AppSkin.Icon(sendCenter, IconGlyph.Of(FontAwesomeIcon.PaperPlane), White, 0.9f);
        }

        if (submitted && canSend)
        {
            if (editTargetId is { } editId)
            {
                model.OnEditText(model.ConversationId, editId, draft);
                ClearEdit();
            }
            else
            {
                if (hasDraft)
                {
                    model.OnSendText(model.ConversationId, draft, replyTargetId);
                    UiFeedback.Play(UiSound.MessageSent);
                    draft = string.Empty;
                    ClearReply();
                }

                if (pastedImagePath is { } pastePath)
                {
                    model.OnSendImage(model.ConversationId, pastePath);
                    DetachPastedImage();
                }
            }

            emojiOpen = false;
            focus = true;
        }

        if (emojiOpen)
        {
            DrawEmojiPanel(area, model);
        }
    }

    private void DrawEmojiPanel(Rect composerArea, in ChatComposerModel model)
    {
        var scale = UiScale.Current;
        var height = 250f * scale;
        var bottom = composerArea.Min.Y - AccessoryHeight;
        var panel = new Rect(new Vector2(composerArea.Min.X, bottom - height),
            new Vector2(composerArea.Max.X, bottom));
        var picked = emojiPicker.Draw(panel, model.Ui);
        if (picked is null)
        {
            return;
        }

        if (draft.Length + picked.Length < model.MaxLength)
        {
            draft += picked;
            Plugin.Fonts.NoticeText(draft);
        }
    }

    private void PumpPendingPaste()
    {
        if (!pasteReadDone)
        {
            return;
        }

        pasteReadDone = false;
        pasteBusy = false;
        var bytes = pasteReadResult;
        pasteReadResult = null;
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        AttachPastedImage(bytes);
    }

    private void AttachPastedImage(byte[] pngBytes)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"aetherphone-paste-{Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, pngBytes);
            pastedImagePath = path;
        }
        catch (Exception exception)
        {
            AepLog.Warning(exception, "Could not write pasted image temp file");
            return;
        }

        var generation = ++pasteGeneration;
        _ = ImageProcessor.DecodeToTextureAsync(Plugin.TextureProvider, pngBytes, "chatcomposer.paste",
            ImageProcessor.MaxDecodePixels, PasteMaxDimension, CancellationToken.None).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully)
            {
                return;
            }

            var texture = task.Result;
            if (generation != pasteGeneration)
            {
                texture.Dispose();
                return;
            }

            pastedTexture = texture;
        });
    }

    private void DiscardPastedImage()
    {
        pasteGeneration++;
        pastedTexture?.Dispose();
        pastedTexture = null;
        if (pastedImagePath is { } path)
        {
            pastedImagePath = null;
            TryDeleteFile(path);
        }
    }

    // Hands the temp file to the store; the send completion callback deletes it.
    private void DetachPastedImage()
    {
        pasteGeneration++;
        pastedTexture?.Dispose();
        pastedTexture = null;
        pastedImagePath = null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            AepLog.Warning(exception, $"Could not delete temp file {path}");
        }
    }

    private void DrawPastedBar(Rect area, in ChatComposerModel model)
    {
        var ui = model.Ui;
        var theme = ui.Theme;
        var scale = UiScale.Current;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(area.Min, area.Max, ImGui.GetColorU32(BarFill));
        drawList.AddLine(area.Min, new Vector2(area.Max.X, area.Min.Y), ImGui.GetColorU32(theme.Separator), 1f);
        var inset = 10f * scale;
        var thumbSize = 56f * scale;
        var thumbMin = new Vector2(area.Min.X + inset, area.Center.Y - thumbSize * 0.5f);
        var thumbMax = thumbMin + new Vector2(thumbSize, thumbSize);
        var texture = pastedTexture;
        if (texture is not null)
        {
            var size = texture.Size;
            var aspect = size.X > 0f && size.Y > 0f ? size.X / size.Y : 1f;
            var drawn = aspect >= 1f
                ? new Vector2(thumbSize, thumbSize / aspect)
                : new Vector2(thumbSize * aspect, thumbSize);
            var imageMin = new Vector2(area.Min.X + inset + (thumbSize - drawn.X) * 0.5f,
                area.Center.Y - drawn.Y * 0.5f);
            drawList.AddImageRounded(texture.Handle, imageMin, imageMin + drawn, Vector2.Zero, Vector2.One,
                0xFFFFFFFFu, 8f * scale, ImDrawFlags.RoundCornersAll);
        }
        else
        {
            Squircle.Fill(drawList, thumbMin, thumbMax, 10f * scale, ImGui.GetColorU32(FieldFill));
            AppSkin.Icon(new Vector2(area.Min.X + inset + thumbSize * 0.5f, area.Center.Y),
                FontAwesomeIcon.Image.ToIconString(), ui.MutedInk, 0.9f);
        }

        var textLeft = thumbMax.X + 12f * scale;
        var closeRadius = 13f * scale;
        var closeCenter = new Vector2(area.Max.X - 14f * scale - closeRadius, area.Center.Y);
        var textWidth = closeCenter.X - 16f * scale - textLeft;
        Typography.Draw(new Vector2(textLeft, area.Min.Y + 12f * scale),
            Typography.FitText(Loc.T(L.Message.PasteImageReady), textWidth, 0.9f, FontWeight.SemiBold),
            theme.TextStrong, 0.9f, FontWeight.SemiBold);
        Typography.Draw(new Vector2(textLeft, area.Min.Y + 36f * scale),
            Typography.FitText(Loc.T(L.Velvet.SendPicture), textWidth, 0.8f, FontWeight.Regular),
            ui.MutedInk, 0.8f);
        if (ui.IconButton(closeCenter, closeRadius, FontAwesomeIcon.Times.ToIconString(), ui.MutedInk,
                AppSkin.Transparent, 0.9f, Loc.T(L.Common.Cancel)))
        {
            DiscardPastedImage();
        }
    }

    private void DrawRecordingComposer(Rect area, in ChatComposerModel model)
    {
        var ui = model.Ui;
        var theme = ui.Theme;
        var scale = UiScale.Current;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(area.Min, new Vector2(area.Max.X, area.Min.Y), ImGui.GetColorU32(theme.Separator), 1f);
        var cancelCenter = new Vector2(area.Min.X + 28f * scale, area.Center.Y);
        if (ui.IconButton(cancelCenter, 16f * scale, IconGlyph.Of(FontAwesomeIcon.TrashAlt), theme.Danger,
                AppSkin.Transparent, 1f, Loc.T(L.Common.Cancel), HoverLabelSide.Above))
        {
            recorder.Cancel();
            UiFeedback.Play(UiSound.RecordCancel);
            return;
        }

        var pulse = 0.55f + 0.45f * MathF.Sin((float)ImGui.GetTime() * 5f);
        var dotCenter = new Vector2(cancelCenter.X + 34f * scale, area.Center.Y);
        drawList.AddCircleFilled(dotCenter, 5f * scale,
            ImGui.GetColorU32(Palette.WithAlpha(theme.Danger, 0.4f + 0.6f * pulse)), 16);
        var elapsed = TimeText.MinutesSeconds((int)recorder.ElapsedSeconds);
        Typography.Draw(new Vector2(dotCenter.X + 12f * scale, area.Center.Y
            - Typography.Measure(elapsed, 1f, FontWeight.SemiBold).Y * 0.5f), elapsed, theme.TextStrong, 1f,
            FontWeight.SemiBold);
        var meterLeft = dotCenter.X + 64f * scale;
        var meterRight = area.Max.X - 64f * scale;
        if (meterRight > meterLeft + 30f * scale)
        {
            var meterY = area.Center.Y;
            drawList.AddRectFilled(new Vector2(meterLeft, meterY - 2f * scale),
                new Vector2(meterRight, meterY + 2f * scale), ImGui.GetColorU32(FieldFill), 2f * scale);
            var level = Math.Clamp(recorder.Level * 6f, 0f, 1f);
            drawList.AddRectFilled(new Vector2(meterLeft, meterY - 2f * scale),
                new Vector2(meterLeft + (meterRight - meterLeft) * level, meterY + 2f * scale),
                ImGui.GetColorU32(ui.Accent), 2f * scale);
        }

        var sendCenter = new Vector2(area.Max.X - 28f * scale, area.Center.Y);
        drawList.AddCircleFilled(sendCenter, 16f * scale, ImGui.GetColorU32(ui.Accent), 24);
        AppSkin.Icon(sendCenter, IconGlyph.Of(FontAwesomeIcon.PaperPlane), White, 0.9f);
        var sendRect = new Rect(sendCenter - new Vector2(16f * scale, 16f * scale),
            sendCenter + new Vector2(16f * scale, 16f * scale));
        HoverTooltip.Show(sendRect, Loc.T(L.Velvet.Send), HoverLabelSide.Above);
        var sendClicked = UiInteract.HoverClick(sendRect.Min, sendRect.Max);
        if (sendClicked || recorder.AtCapacity)
        {
            if (recorder.Stop(out var wavBytes, out var durationSecs))
            {
                model.OnSendVoice(model.ConversationId, wavBytes, durationSecs);
                UiFeedback.Play(UiSound.MessageSent);
            }
        }
    }

    private void DrawReplyBar(Rect area, in ChatComposerModel model)
    {
        var ui = model.Ui;
        var theme = ui.Theme;
        var scale = UiScale.Current;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(area.Min, area.Max, ImGui.GetColorU32(BarFill));
        drawList.AddLine(area.Min, new Vector2(area.Max.X, area.Min.Y), ImGui.GetColorU32(theme.Separator), 1f);
        var barMin = new Vector2(area.Min.X + 14f * scale, area.Min.Y + 8f * scale);
        var barMax = new Vector2(barMin.X + 3f * scale, area.Max.Y - 8f * scale);
        Squircle.Fill(drawList, barMin, barMax, 1.5f * scale, ImGui.GetColorU32(ui.Accent));
        var textLeft = barMax.X + 9f * scale;
        var closeRadius = 13f * scale;
        var textWidth = area.Max.X - 20f * scale - closeRadius * 2f - textLeft;
        Typography.Draw(new Vector2(textLeft, area.Min.Y + 7f * scale),
            Typography.FitText(Loc.T(L.Message.ReplyingTo, replyBarName), textWidth, 0.78f, FontWeight.SemiBold),
            ui.Accent, 0.78f, FontWeight.SemiBold);
        Typography.Draw(new Vector2(textLeft, area.Min.Y + 24f * scale),
            Typography.FitText(replyBarPreview, textWidth, 0.82f, FontWeight.Regular), ui.MutedInk, 0.82f);
        var closeCenter = new Vector2(area.Max.X - 14f * scale - closeRadius, area.Center.Y);
        if (ui.IconButton(closeCenter, closeRadius, IconGlyph.Of(FontAwesomeIcon.Times), ui.MutedInk,
                AppSkin.Transparent, 0.9f, Loc.T(L.Common.Cancel))
            || (model.CanHandleEscape && ImGui.IsKeyPressed(ImGuiKey.Escape)))
        {
            ClearReply();
        }
    }

    private void DrawEditBar(Rect area, in ChatComposerModel model)
    {
        var ui = model.Ui;
        var theme = ui.Theme;
        var scale = UiScale.Current;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(area.Min, area.Max, ImGui.GetColorU32(BarFill));
        drawList.AddLine(area.Min, new Vector2(area.Max.X, area.Min.Y), ImGui.GetColorU32(theme.Separator), 1f);
        var iconCenter = new Vector2(area.Min.X + 22f * scale, area.Center.Y);
        AppSkin.Icon(iconCenter, IconGlyph.Of(FontAwesomeIcon.Pen), ui.Accent, 0.9f);
        var textLeft = iconCenter.X + 16f * scale;
        var closeRadius = 13f * scale;
        var textWidth = area.Max.X - 20f * scale - closeRadius * 2f - textLeft;
        Typography.Draw(new Vector2(textLeft, area.Min.Y + 7f * scale),
            Typography.FitText(Loc.T(L.Message.EditingLabel), textWidth, 0.78f, FontWeight.SemiBold),
            ui.Accent, 0.78f, FontWeight.SemiBold);
        Typography.Draw(new Vector2(textLeft, area.Min.Y + 24f * scale),
            Typography.FitText(editBarPreview, textWidth, 0.82f, FontWeight.Regular), ui.MutedInk, 0.82f);
        var closeCenter = new Vector2(area.Max.X - 14f * scale - closeRadius, area.Center.Y);
        if (ui.IconButton(closeCenter, closeRadius, IconGlyph.Of(FontAwesomeIcon.Times), ui.MutedInk,
                AppSkin.Transparent, 0.9f, Loc.T(L.Common.Cancel))
            || (model.CanHandleEscape && ImGui.IsKeyPressed(ImGuiKey.Escape)))
        {
            ClearEdit();
        }
    }
}
