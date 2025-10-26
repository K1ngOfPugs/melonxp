using ARMeilleure.Memory;
using Ryujinx.Cpu.Jit.HostTracked;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Ryujinx.Common.Logging;

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Represents a CPU memory manager which maps guest virtual memory directly onto a host virtual region.
    /// </summary>
    public sealed class MemoryManagerNative : VirtualMemoryManagerRefCountedBase, IMemoryManager, IVirtualMemoryManagerTracked, IWritableBlock
    {
        private readonly MemoryBlock _backingMemory;
        private readonly PageTable<ulong> _pageTable;

        private readonly ManagedPageFlags _pages;

        private readonly NativePageTable _nativePageTable;
        private readonly Ryujinx.Cpu.Jit.HostTracked.AddressSpacePartitioned _addressSpace;

        /// <inheritdoc/>
        public bool UsesPrivateAllocations => true;
        private readonly bool _useProtectionMirrors;

        public IntPtr PageTablePointer => _nativePageTable.PageTablePointer;

        public ulong ReservedSize => 0;

        public MemoryManagerType Type => MemoryManagerType.HostMappedUnsafe;

        public MemoryTracking Tracking { get; }

        public event Action<ulong, ulong> UnmapEvent;

        public int AddressSpaceBits { get; }
        protected override ulong AddressSpaceSize { get; }

        /// <summary>
        /// Creates a new instance of the host mapped memory manager.
        /// </summary>
        /// <param name="backingMemory">Physical backing memory where virtual memory will be mapped to</param>
        /// <param name="addressSpaceSize">Size of the address space</param>
        /// <param name="invalidAccessHandler">Optional function to handle invalid memory accesses</param>
        public MemoryManagerNative(
            MemoryBlock backingMemory,
            ulong addressSpaceSize,
            InvalidAccessHandler invalidAccessHandler = null)
        {
        bool useProtectionMirrors = MemoryBlock.GetPageSize() > PageSize;
        _useProtectionMirrors = useProtectionMirrors;

            _backingMemory = backingMemory;
            _pageTable = new PageTable<ulong>();
            AddressSpaceSize = addressSpaceSize;

            ulong asSize = PageSize;
            int asBits = PageBits;

            while (asSize < addressSpaceSize)
            {
                asSize <<= 1;
                asBits++;
            }

            AddressSpaceBits = asBits;

            _pages = new ManagedPageFlags(asBits);
            _nativePageTable = new NativePageTable(asSize);

            Tracking = new MemoryTracking(this, PageSize, invalidAccessHandler, true);
            _addressSpace = new AddressSpacePartitioned(Tracking, backingMemory, _nativePageTable, true);
        }

        /// <inheritdoc/>
        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            Console.WriteLine($"Map called: va=0x{va:X16}, pa=0x{pa:X16}, size=0x{size:X16}, AddressSpaceSize=0x{AddressSpaceSize:X16}");
            AssertValidAddressAndSize(va, size);

            if (flags.HasFlag(MemoryMapFlags.Private))
            {
                _addressSpace.Map(va, pa, size);
            }

            _pages.AddMapping(va, size);
            _nativePageTable.Map(va, pa, size, _addressSpace, _backingMemory, flags.HasFlag(MemoryMapFlags.Private));
            PtMap(va, pa, size);

            Tracking.Map(va, size);
        }

        private void PtMap(ulong va, ulong pa, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Map(va, pa);

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
        }

        /// <inheritdoc/>
        public void Unmap(ulong va, ulong size)
        {
            AssertValidAddressAndSize(va, size);

            _addressSpace.Unmap(va, size);

            UnmapEvent?.Invoke(va, size);
            Tracking.Unmap(va, size);

            _pages.RemoveMapping(va, size);
            _nativePageTable.Unmap(va, size);
            PtUnmap(va, size);
        }

        private void PtUnmap(ulong va, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Unmap(va);

                va += PageSize;
                size -= PageSize;
            }
        }

        /// <inheritdoc/>
        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            if (!IsRangeMapped(va, size))
            {
                Console.WriteLine($"Warning: Attempting to reprotect unmapped memory at 0x{va:X16}, size 0x{size:X16}");
                Map(va, 0, size, MemoryMapFlags.None);
            }

            Console.WriteLine($"Reprotecting VA 0x{va:X16}, size 0x{size:X16}, protection={protection}");

            _addressSpace.Reprotect(va, size, protection);
            MemoryManagement.Reprotect((nint)GetPhysicalAddress(va), size, protection, false, false);

            _pages.TrackingReprotect(va, size, protection);
        }

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();

            SignalMemoryTracking(va, (ulong)size, true);

            if (_useProtectionMirrors)
            {
                // When using protection mirrors, get pointer from address space
                IntPtr ptr = _addressSpace.GetPointer(va, (ulong)size);
                unsafe
                {
                    return ref Unsafe.AsRef<T>((void*)ptr);
                }
            }
            else
            {
                if (!TryGetVirtualContiguous(va, size, out MemoryBlock memory, out ulong offset))
                {
                    ThrowMemoryNotContiguous();
                }

                return ref memory.GetRef<T>(offset);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsMapped(ulong va)
        {
            return ValidateAddress(va) && _pages.IsMapped(va);
        }

        /// <inheritdoc/>
        public bool IsRangeMapped(ulong va, ulong size)
        {
            AssertValidAddressAndSize(va, size);

            return _pages.IsRangeMapped(va, size);
        }

        private bool TryGetVirtualContiguous(ulong va, int size, out MemoryBlock memory, out ulong offset)       
        {    
            if (_addressSpace.HasAnyPrivateAllocation(va, (ulong)size, out PrivateRange range))
            {
                if (range.Memory != null)
                {
                    memory = range.Memory;
                    offset = range.Offset;
                    return true;
                }

                memory = null;
                offset = 0;
                return false;
            }

            memory = _backingMemory;
            offset = GetPhysicalAddressInternal(va);

            return IsPhysicalContiguous(va, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsPhysicalContiguous(ulong va, int size)
        {
            if (!ValidateAddress(va) || !ValidateAddressAndSize(va, (ulong)size))
            {
                return false;
            }

            int pages = GetPagesCount(va, (uint)size, out va);

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return false;
                }

                if (GetPhysicalAddressInternal(va) + PageSize != GetPhysicalAddressInternal(va + PageSize))
                {
                    return false;
                }

                va += PageSize;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong GetContiguousSize(ulong va, ulong size)
        {
            ulong contiguousSize = PageSize - (va & PageMask);

            if (!ValidateAddress(va) || !ValidateAddressAndSize(va, size))
            {
                return contiguousSize;
            }

            int pages = GetPagesCount(va, size, out va);

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return contiguousSize;
                }

                if (GetPhysicalAddressInternal(va) + PageSize != GetPhysicalAddressInternal(va + PageSize))
                {
                    return contiguousSize;
                }

                va += PageSize;
                contiguousSize += PageSize;
            }

            return Math.Min(contiguousSize, size);
        }

        private (MemoryBlock, ulong, ulong) GetMemoryOffsetAndSize(ulong va, ulong size)
        {
            PrivateRange privateRange = _addressSpace.GetFirstPrivateAllocation(va, size, out ulong nextVa);

            if (privateRange.Memory != null)
            {
                return (privateRange.Memory, privateRange.Offset, privateRange.Size);
            }

            ulong physSize = GetContiguousSize(va, Math.Min(size, nextVa - va));

            return (_backingMemory, GetPhysicalAddressChecked(va), physSize);
        }

        /// <inheritdoc/>
        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                return Enumerable.Empty<HostMemoryRange>();
            }

            if (!ValidateAddressAndSize(va, size))
            {
                return null;
            }

            var regions = new List<HostMemoryRange>();
            ulong endVa = va + size;

            try
            {
                while (va < endVa)
                {
                    (MemoryBlock memory, ulong rangeOffset, ulong rangeSize) = GetMemoryOffsetAndSize(va, endVa - va);

                    regions.Add(new HostMemoryRange((nuint)memory.GetPointer(rangeOffset, rangeSize), rangeSize));

                    va += rangeSize;
                }
            }
            catch (InvalidMemoryRegionException)
            {
                return null;
            }

            return regions;
        }

        /// <inheritdoc/>
        public IEnumerable<MemoryRange> GetPhysicalRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                return Enumerable.Empty<MemoryRange>();
            }

            return GetPhysicalRegionsImpl(va, size);
        }

        private List<MemoryRange> GetPhysicalRegionsImpl(ulong va, ulong size)
        {
            if (!ValidateAddress(va) || !ValidateAddressAndSize(va, size))
            {
                return null;
            }

            int pages = GetPagesCount(va, (uint)size, out va);

            var regions = new List<MemoryRange>();

            ulong regionStart = GetPhysicalAddressInternal(va);
            ulong regionSize = PageSize;

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return null;
                }

                ulong newPa = GetPhysicalAddressInternal(va + PageSize);

                if (GetPhysicalAddressInternal(va) + PageSize != newPa)
                {
                    regions.Add(new MemoryRange(regionStart, regionSize));
                    regionStart = newPa;
                    regionSize = 0;
                }

                va += PageSize;
                regionSize += PageSize;
            }

            regions.Add(new MemoryRange(regionStart, regionSize));

            return regions;
        }

        private ulong GetPhysicalAddressChecked(ulong va)
        {
            if (!IsMapped(va))
            {
                ThrowInvalidMemoryRegionException($"Not mapped: va=0x{va:X16}");
            }

            return GetPhysicalAddressInternal(va);
        }

        private ulong GetPhysicalAddressInternal(ulong va)
        {
            if (_useProtectionMirrors)
            {
                // When using protection mirrors, return the actual host pointer
                IntPtr ptr = _addressSpace.GetPointer(va, 1);
                return (ulong)ptr;
            }
            else
            {
                // Without protection mirrors, use traditional approach
                nuint pa = TranslateVirtualAddressChecked(va);
                return (ulong)_backingMemory.GetPointer(pa, 1);
            }
        }

        public ulong GetPhysicalAddress(ulong va)
        {
            AssertValidAddressAndSize(va, 1);

            if (_useProtectionMirrors)
            {
                IntPtr ptr = _addressSpace.GetPointer(va, 1);
                Logger.Info?.Print(LogClass.Cpu, $"GetPhysicalAddress: VA=0x{va:X16} -> Ptr=0x{(ulong)ptr:X16}");
                return (ulong)ptr;
            }
            else
            {
                nuint pa = TranslateVirtualAddressChecked(va);
                return (ulong)_backingMemory.GetPointer(pa, 1);
            }
        }


        /// <inheritdoc/>
        /// <remarks>
        /// This function also validates that the given range is both valid and mapped, and will throw if it is not.
        /// </remarks>
        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            AssertValidAddressAndSize(va, size);

            if (precise)
            {
                Tracking.VirtualMemoryEvent(va, size, write, precise: true, exemptId);
                return;
            }

            _pages.SignalMemoryTracking(Tracking, va, size, write, exemptId);
        }

        /// <inheritdoc/>
        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
            if (guest)
            {
                _addressSpace.Reprotect(va, size, protection);
            }
            else
            {
                _pages.TrackingReprotect(va, size, protection);
            }
        }

        /// <inheritdoc/>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags)
        {
            return Tracking.BeginTracking(address, size, id, flags);
        }

        /// <inheritdoc/>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags)
        {
            return Tracking.BeginGranularTracking(address, size, handles, granularity, id, flags);
        }

        /// <inheritdoc/>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            return Tracking.BeginSmartGranularTracking(address, size, granularity, id);
        }

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
        {
            if (_useProtectionMirrors)
            {
                // When using protection mirrors, 'pa' is actually a virtual address
                // We need to get the correct pointer from the address space
                ulong va = (ulong)pa;
                IntPtr ptr = _addressSpace.GetPointer(va, (ulong)size);

                unsafe
                {
                    return new UnmanagedMemoryManager<byte>((byte*)ptr, size).Memory;
                }
            }

            return _backingMemory.GetMemory(pa, size);
        }

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
        {
            if (_useProtectionMirrors)
            {
                // When using protection mirrors, 'pa' is actually a virtual address
                // We need to get the correct pointer from the address space
                ulong va = (ulong)pa;
                IntPtr ptr = _addressSpace.GetPointer(va, (ulong)size);

                unsafe
                {
                    return new Span<byte>((void*)ptr, size);
                }
            }

            return _backingMemory.GetSpan(pa, size);
        }

        protected override nuint TranslateVirtualAddressChecked(ulong va)
        {
            if (_useProtectionMirrors)
            {
                // When using protection mirrors, we pass through the VA
                // GetPhysicalAddressSpan/Memory will handle getting the right pointer
                if (!IsMapped(va))
                {
                    ThrowInvalidMemoryRegionException($"Not mapped: va=0x{va:X16}");
                }
                return (nuint)va;
            }

            return (nuint)GetPhysicalAddressChecked(va);
        }

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
        {
            if (_useProtectionMirrors)
            {
                // When using protection mirrors, we pass through the VA
                // GetPhysicalAddressSpan/Memory will handle getting the right pointer
                return (nuint)va;
            }

            return (nuint)GetPhysicalAddressInternal(va);
        }



        /// <summary>
        /// Disposes of resources used by the memory manager.
        /// </summary>
        protected override void Destroy()
        {
            _addressSpace.Dispose();
            _nativePageTable.Dispose();
        }

        // protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
            // => _backingMemory.GetMemory(pa, size);

        // protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
            // => _backingMemory.GetSpan(pa, size);

        // protected override nuint TranslateVirtualAddressChecked(ulong va)
            // => (nuint)GetPhysicalAddressChecked(va);

        // protected override nuint TranslateVirtualAddressUnchecked(ulong va)
            // => (nuint)GetPhysicalAddressInternal(va);
    }
}