using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class RelayHost
{
    private readonly DirectoryClient _directory;
    private readonly DirectorySettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, Seat> _seats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Candidate, string? Mid, ushort? Line)>> _waiting = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Seat(RelaySession session, CancellationTokenSource stop)
    {
        public RelaySession Session { get; } = session;
        public CancellationTokenSource Stop { get; } = stop;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public RelayHost(DirectoryClient directory, DirectorySettings settings)
    {
        _directory = directory;
        _settings = settings;
        directory.SignalReceived += OnSignal;
        directory.SasReceived += _ =>
        {
            if (!OpenViews.Any)
            {
                ConsoleGate.Audit("-", "secure-attention", "refused without a session");
                return;
            }

            SessionLaunch.SendSecureAttention();
            ConsoleGate.Audit("-", "secure-attention", "sent");
        };
    }

    private void OnSignal(string from, string payload)
    {
        _ = Task.Run(() => AcceptAsync(from, payload));
    }

    private async Task AcceptAsync(string from, string payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            var kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : "";
            if (kind == "candidate" && root.TryGetProperty("candidate", out var candidate))
            {
                ushort? line = null;
                if (root.TryGetProperty("sdpMLineIndex", out var index) && index.TryGetUInt16(out var value))
                    line = value;
                var mid = root.TryGetProperty("sdpMid", out var midValue) ? midValue.GetString() : null;
                var text = candidate.GetString() ?? "";
                RelaySession? session = null;
                lock (_gate)
                {
                    if (_seats.TryGetValue(from, out var seat))
                        session = seat.Session;
                    else
                    {
                        if (!_waiting.TryGetValue(from, out var list))
                        {
                            list = [];
                            _waiting[from] = list;
                        }

                        list.Add((text, mid, line));
                    }
                }

                session?.AddCandidate(text, mid, line);
                return;
            }

            if (kind != "offer" || !root.TryGetProperty("sdp", out var sdp) || string.IsNullOrWhiteSpace(from))
                return;

            Seat? previous;
            lock (_gate)
                _seats.Remove(from, out previous);
            if (previous is not null)
            {
                previous.Stop.Cancel();
                try { await previous.Done.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception) { }
                await previous.Session.DisposeAsync();
            }

            var sessionNew = new RelaySession();
            var mine = new CancellationTokenSource();
            var seatNew = new Seat(sessionNew, mine);
            List<(string Candidate, string? Mid, ushort? Line)> waiting = [];
            lock (_gate)
            {
                _seats[from] = seatNew;
                if (_waiting.Remove(from, out var found))
                    waiting = found;
            }

            foreach (var item in waiting)
                sessionNew.AddCandidate(item.Candidate, item.Mid, item.Line);

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(mine.Token);
                limit.CancelAfter(TimeSpan.FromMinutes(30));
                await sessionNew.AcceptAsync(_directory, from, sdp.GetString() ?? "", _settings, relayOnly: false, limit.Token);
                var raw = await sessionNew.Opened.WaitAsync(TimeSpan.FromSeconds(25));
                var stream = await LinkSeal.OpenServerAsync(raw, LinkWorker.ActivePipeKey(), limit.Token);
                await LinkWorker.ServeAsync(stream, limit.Token, sessionNew, _directory, LinkWorker.TrustsLocalNetwork());
            }
            finally
            {
                lock (_gate)
                {
                    if (_seats.TryGetValue(from, out var current) && ReferenceEquals(current, seatNew))
                        _seats.Remove(from);
                }

                await sessionNew.DisposeAsync();
                mine.Dispose();
                seatNew.Done.TrySetResult();
            }
        }
        catch (Exception)
        {
        }
    }
}
