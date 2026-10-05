using System;

namespace AstralPartyChatPlugin;

/// <summary>
/// Converts keyboard and explicit-send events into one-frame submit tokens.
/// It has no Unity dependencies so its IME behavior can be tested directly.
/// </summary>
internal sealed class ChatInputSubmitState
{
    private const int SubmitLifetimeFrames = 300;

    private int _nextSequence;
    private int _lastEnterFrame = int.MinValue;
    private bool _wasComposing;
    private PendingSubmit? _pending;

    /// <summary>Queues Enter, waiting for IME text to commit before sending.</summary>
    public int RequestEnter(int frame, bool compositionActive)
    {
        ObserveFrame(frame, compositionActive);
        // Raw Input polling and InputField's native key event can report the
        // same press. Keep its token and original ready frame unchanged.
        if (_lastEnterFrame == frame)
            return _pending?.Sequence ?? 0;
        _lastEnterFrame = frame;

        // Native and raw reports can straddle the IME commit frame. Preserve
        // the pending send instead of requiring another press or replacing it.
        if (_pending.HasValue)
            return _pending.Value.Sequence;

        return Queue(frame + 1, frame + SubmitLifetimeFrames, explicitSend: false);
    }

    /// <summary>
    /// Queues a button click. It waits for active IME composition to commit,
    /// then becomes ready on the following frame so InputField can accept it.
    /// </summary>
    public int RequestExplicitSend(int frame)
    {
        return Queue(frame + 1, frame + SubmitLifetimeFrames, explicitSend: true);
    }

    /// <summary>Focus loss cancels keyboard submits but retains a send-button request.</summary>
    public void CancelEnter()
    {
        if (_pending.HasValue && !_pending.Value.ExplicitSend)
            _pending = null;
    }

    /// <summary>Returns and consumes a ready submit token, or false while waiting.</summary>
    public bool TryTakeReady(int frame, bool compositionActive, out int sequence)
    {
        sequence = 0;
        ObserveFrame(frame, compositionActive);

        if (!_pending.HasValue)
            return false;

        var pending = _pending.Value;
        if (frame > pending.ExpiresAfterFrame)
        {
            _pending = null;
            return false;
        }

        if (compositionActive || frame < pending.ReadyAfterFrame)
            return false;

        _pending = null;
        sequence = pending.Sequence;
        return true;
    }

    public void Reset()
    {
        _pending = null;
        _wasComposing = false;
        _lastEnterFrame = int.MinValue;
    }

    private int Queue(int readyAfterFrame, int expiresAfterFrame, bool explicitSend)
    {
        var sequence = unchecked(++_nextSequence);
        if (sequence == 0)
            sequence = unchecked(++_nextSequence);

        _pending = new PendingSubmit(
            sequence,
            readyAfterFrame,
            expiresAfterFrame,
            explicitSend);
        return sequence;
    }

    public void ObserveFrame(int frame, bool compositionActive)
    {
        if (compositionActive)
        {
            _wasComposing = true;
            return;
        }

        if (!_wasComposing)
            return;

        _wasComposing = false;
        if (_pending.HasValue)
        {
            var pending = _pending.Value;
            pending.ReadyAfterFrame = Math.Max(pending.ReadyAfterFrame, frame + 1);
            _pending = pending;
        }
    }

    private struct PendingSubmit
    {
        public PendingSubmit(
            int sequence,
            int readyAfterFrame,
            int expiresAfterFrame,
            bool explicitSend)
        {
            Sequence = sequence;
            ReadyAfterFrame = readyAfterFrame;
            ExpiresAfterFrame = expiresAfterFrame;
            ExplicitSend = explicitSend;
        }

        public int Sequence { get; }
        public int ReadyAfterFrame { get; set; }
        public int ExpiresAfterFrame { get; }
        public bool ExplicitSend { get; }
    }
}
