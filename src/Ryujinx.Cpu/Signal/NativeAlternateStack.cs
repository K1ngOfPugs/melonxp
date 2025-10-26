using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Signal
{
    static partial class NativeAlternateStackRegistration
    {
        private const int ALTSTACK_SUCCESS = 0;
        private const int ALTSTACK_ERROR_SIZE_TOO_SMALL = -1;
        private const int ALTSTACK_ERROR_SIGALTSTACK_FAILED = -2;

        private const string LibraryName = "RyujinxHelper.framework/RyujinxHelper";

        [LibraryImport(LibraryName, EntryPoint = "RegisterAlternateStack")]
        private static partial int RegisterAlternateStackNative(IntPtr stackPtr, ulong stackSize);

        [LibraryImport(LibraryName, EntryPoint = "UnregisterAlternateStack")]
        private static partial int UnregisterAlternateStackNative();

        [LibraryImport(LibraryName, EntryPoint = "GetLastAlternateStackErrno")]
        private static partial int GetLastAlternateStackErrno();

        public static void RegisterAlternateStack(IntPtr stackPtr, ulong stackSize)
        {
            return;
            int result = RegisterAlternateStackNative(stackPtr, stackSize);

            switch (result)
            {
                case ALTSTACK_SUCCESS:
                    return;
                
                case ALTSTACK_ERROR_SIZE_TOO_SMALL:
                    throw new ArgumentException("stackSize too small (must be >= 16384)", nameof(stackSize));
                
                case ALTSTACK_ERROR_SIGALTSTACK_FAILED:
                    int errno = GetLastAlternateStackErrno();
                    throw new SystemException($"Could not set alternate stack. (errno: {errno})");
                
                default:
                    throw new SystemException($"Unknown error occurred. Result code: {result}");
            }
        }

        public static void UnregisterAlternateStack()
        {
            int result = UnregisterAlternateStackNative();

            if (result != ALTSTACK_SUCCESS)
            {
                int errno = GetLastAlternateStackErrno();
                throw new SystemException($"Could not remove alternate stack. (errno: {errno})");
            }
        }
    }
}