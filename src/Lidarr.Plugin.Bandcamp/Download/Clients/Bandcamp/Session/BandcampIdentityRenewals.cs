using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    // Process-local receipts contain no candidate, credential or session fingerprint.
    // Only Begin creates IDs. Unknown/expired IDs can never submit work, including after restart.
    internal static class BandcampIdentityRenewals
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<Guid, Operation> Operations = new();

        public static Operation? Begin(int ownerId, string ownerName)
        {
            lock (Gate)
            {
                foreach (var id in Operations.Where(p => p.Value.Expires <= DateTime.UtcNow &&
                    p.Value.State is not ("Validating" or "Committing")).Select(p => p.Key).ToArray())
                    Operations.Remove(id);
                if (Operations.Count >= 4096) return null;
                var operation = new Operation(ownerId, ownerName);
                Operations.Add(operation.Id, operation);
                return operation;
            }
        }

        public static Operation? Find(Guid id, int ownerId, string ownerName)
        {
            lock (Gate)
                return Operations.TryGetValue(id, out var operation) && operation.OwnerId == ownerId &&
                    operation.OwnerName == ownerName ? operation : null;
        }

        internal sealed class Operation
        {
            private readonly object _gate = new();
            private string _state = "Pending";
            private string? _problem;
            public Guid Id { get; } = Guid.NewGuid();
            public int OwnerId { get; }
            public string OwnerName { get; }
            public DateTime Expires { get; } = DateTime.UtcNow.AddHours(1);
            public string State { get { lock (_gate) return _state; } }

            public Operation(int ownerId, string ownerName) { OwnerId = ownerId; OwnerName = ownerName; }

            public bool Start()
            {
                lock (_gate)
                {
                    if (_state != "Pending") return false;
                    if (Expires <= DateTime.UtcNow) { _state = "Cancelled"; return false; }
                    _state = "Validating";
                    return true;
                }
            }

            public bool TryBeginCommit()
            {
                lock (_gate)
                {
                    if (_state != "Validating") return false;
                    if (Expires <= DateTime.UtcNow) { _state = "Cancelled"; return false; }
                    _state = "Committing";
                    return true;
                }
            }

            public void Cancel()
            {
                lock (_gate)
                    if (_state is "Pending" or "Validating") _state = "Cancelled";
            }

            public void Complete(bool success, string? problem)
            {
                lock (_gate)
                {
                    if (_state == "Cancelled") return;
                    // An exception during persistence can lose the acknowledgement after a DB commit.
                    _state = success ? "Committed" : _state == "Committing" && problem is "StorageFailure" or "RecoveryFailed"
                        ? "Unknown" : "Refused";
                    _problem = problem;
                }
            }

            public object Receipt()
            {
                lock (_gate) return new { OperationId = Id.ToString("D"), State = _state, Success = _state == "Committed", Problem = _problem };
            }
        }
    }
}
