using System;
using System.Collections.Generic;
using System.Linq;

namespace subs2srs.Tests.Harness
{
    /// <summary>
    /// Thread-safe recorder for UtilsMsg.OnShowError / OnShowInfo / OnShowConfirm.
    /// Restores the previous hooks on dispose.
    /// </summary>
    public sealed class RecordingMsgHooks : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<string> _errors = new();
        private readonly List<string> _infos = new();
        private readonly List<string> _confirms = new();

        private readonly Action<string, string>? _prevError;
        private readonly Action<string, string>? _prevInfo;
        private readonly Func<string, string, bool>? _prevConfirm;

        /// <summary>Answer returned by OnShowConfirm. Default: true.</summary>
        public bool ConfirmAnswer { get; set; } = true;

        public RecordingMsgHooks()
        {
            _prevError = UtilsMsg.OnShowError;
            _prevInfo = UtilsMsg.OnShowInfo;
            _prevConfirm = UtilsMsg.OnShowConfirm;

            UtilsMsg.OnShowError = (msg, _) => { lock (_lock) _errors.Add(msg); };
            UtilsMsg.OnShowInfo = (msg, _) => { lock (_lock) _infos.Add(msg); };
            UtilsMsg.OnShowConfirm = (msg, _) =>
            {
                lock (_lock) _confirms.Add(msg);
                return ConfirmAnswer;
            };
        }

        public IReadOnlyList<string> Errors { get { lock (_lock) return _errors.ToList(); } }
        public IReadOnlyList<string> Infos { get { lock (_lock) return _infos.ToList(); } }
        public IReadOnlyList<string> Confirms { get { lock (_lock) return _confirms.ToList(); } }

        public void Clear()
        {
            lock (_lock)
            {
                _errors.Clear();
                _infos.Clear();
                _confirms.Clear();
            }
        }

        public void Dispose()
        {
            UtilsMsg.OnShowError = _prevError!;
            UtilsMsg.OnShowInfo = _prevInfo!;
            UtilsMsg.OnShowConfirm = _prevConfirm!;
        }
    }
}
