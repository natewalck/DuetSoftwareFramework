﻿using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Queued lock/unlock request
    /// </summary>
    public class QueuedLockRequest
    {
        private readonly TaskCompletionSource<bool> _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Indicates if this is a lock or unlock request
        /// </summary>
        public bool IsLockRequest { get; }

        /// <summary>
        /// Indicates if the lock request has been sent to the firmware
        /// </summary>
        public bool IsLockRequested { get; set; }

        /// <summary>
        /// Awaitable task returning true if the lock could be acquired.
        /// It returns false if the controller is reset or an emergency stop occurs
        /// </summary>
        public Task<bool> Task { get => _tcs.Task; }

        /// <summary>
        /// Creates a new queued lock/unlock request instance
        /// </summary>
        /// <param name="isLockRequest">Whether the resource shall be locked</param>
        public QueuedLockRequest(bool isLockRequest) => IsLockRequest = isLockRequest;

        /// <summary>
        /// Resolve the pending task with the given result
        /// </summary>
        /// <param name="lockAcquired">Whether the lock could be acquired</param>
        public void Resolve(bool lockAcquired) =>  _tcs.SetResult(lockAcquired);
    }
}
