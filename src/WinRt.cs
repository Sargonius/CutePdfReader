using System;
using System.Threading.Tasks;
using Windows.Foundation;

namespace CutePdf
{
    // The stock csc.exe can't bind the framework's WinRT await helpers to the split
    // System32\WinMetadata files, so we wait on WinRT operations ourselves.
    static class WinRt
    {
        public static T Wait<T>(IAsyncOperation<T> op)
        {
            return AsTask(op).GetAwaiter().GetResult();
        }

        public static void Wait(IAsyncAction op)
        {
            AsTask(op).GetAwaiter().GetResult();
        }

        static Task<T> AsTask<T>(IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = delegate(IAsyncOperation<T> o, AsyncStatus status)
            {
                try
                {
                    if (status == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                    else if (status == AsyncStatus.Error) tcs.TrySetException(ErrorOf(o));
                    else tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };
            return tcs.Task;
        }

        static Task AsTask(IAsyncAction op)
        {
            var tcs = new TaskCompletionSource<bool>();
            op.Completed = delegate(IAsyncAction o, AsyncStatus status)
            {
                try
                {
                    if (status == AsyncStatus.Completed) tcs.TrySetResult(true);
                    else if (status == AsyncStatus.Error) tcs.TrySetException(ErrorOf(o));
                    else tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };
            return tcs.Task;
        }

        // GetResults() rethrows the failure as the properly mapped .NET exception
        // (FileNotFoundException and so on), which reads better than the raw ErrorCode.
        static Exception ErrorOf<T>(IAsyncOperation<T> o)
        {
            try { o.GetResults(); }
            catch (Exception ex) { return ex; }
            return o.ErrorCode ?? new InvalidOperationException("WinRT operation failed");
        }

        static Exception ErrorOf(IAsyncAction o)
        {
            try { o.GetResults(); }
            catch (Exception ex) { return ex; }
            return o.ErrorCode ?? new InvalidOperationException("WinRT operation failed");
        }
    }
}
