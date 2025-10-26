using System;
using System.Buffers;

public class UnmanagedMemoryManager<T> : MemoryManager<T> where T : unmanaged
{
    private readonly unsafe T* _pointer;
    private readonly int _length;

    public unsafe UnmanagedMemoryManager(T* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public override Span<T> GetSpan()
    {
        unsafe
        {
            return new Span<T>(_pointer, _length);
        }
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        unsafe
        {
            if (elementIndex < 0 || elementIndex >= _length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            return new MemoryHandle(_pointer + elementIndex);
        }
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
