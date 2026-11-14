using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Bandcamp.Protocol;

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
                    p.Value.State is not (BandcampRenewalState.Validating or BandcampRenewalState.Committing)).Select(p => p.Key).ToArray())
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
            private BandcampRenewalState _state = BandcampRenewalState.Pending;
            private string? _problem;
            public Guid Id { get; } = Guid.NewGuid();
            public int OwnerId { get; }
            public string OwnerName { get; }
            public DateTime Expires { get; } = DateTime.UtcNow.AddHours(1);
            public BandcampRenewalState State { get { lock (_gate) return _state; } }

            public Operation(int ownerId, string ownerName) { OwnerId = ownerId; OwnerName = ownerName; }

            public bool Start()
            {
                lock (_gate)
                {
                    if (_state != BandcampRenewalState.Pending) return false;
                    if (Expires <= DateTime.UtcNow) { _state = BandcampRenewalState.Cancelled; return false; }
                    _state = BandcampRenewalState.Validating;
                    return true;
                }
            }

            public bool TryBeginCommit()
            {
                lock (_gate)
                {
                    if (_state != BandcampRenewalState.Validating) return false;
                    if (Expires <= DateTime.UtcNow) { _state = BandcampRenewalState.Cancelled; return false; }
                    _state = BandcampRenewalState.Committing;
                    return true;
                }
            }

            public void Cancel()
            {
                lock (_gate)
                    if (_state is BandcampRenewalState.Pending or BandcampRenewalState.Validating) _state = BandcampRenewalState.Cancelled;
            }

            public void Complete(bool success, string? problem)
            {
                lock (_gate)
                {
                    if (_state == BandcampRenewalState.Cancelled) return;
                    // An exception during persistence can lose the acknowledgement after a DB commit.
                    _state = success ? BandcampRenewalState.Committed : _state == BandcampRenewalState.Committing && problem is "StorageFailure" or "RecoveryFailed"
                        ? BandcampRenewalState.Unknown : BandcampRenewalState.Refused;
                    _problem = problem;
                }
            }

            public object Receipt()
            {
                lock (_gate) return new { OperationId = Id.ToString("D"), State = BandcampRenewalStateWire.Serialize(_state), Success = _state == BandcampRenewalState.Committed, Problem = _problem };
            }
        }
    }
}
