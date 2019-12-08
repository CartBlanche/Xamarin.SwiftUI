using System;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Swift.Interop
{
	/// <summary>
	/// See https://github.com/apple/swift/blob/master/docs/ABI/Mangling.rst#types
	/// </summary>
	public enum SwiftTypeCode {
		Class = 'C',     // nominal class type
		Enum = 'O',      // nominal enum type
		Struct = 'V',    // nominal struct type
	};

	public unsafe class SwiftType
	{
		protected FullTypeMetadata* fullMetadata;

		// Delegates from the value witness table...
		DestroyFunc? _destroy;
		TransferFunc? _copyInit;
		TransferFunc? _moveInit;

		// FIXME: Optimization: Return null from these if type is POD
		internal TransferFunc CopyInitFunc
			=> _copyInit ??= Marshal.GetDelegateForFunctionPointer<TransferFunc> (ValueWitnessTable->InitWithCopy);

		internal TransferFunc MoveInitFunc
			=> _moveInit ??= Marshal.GetDelegateForFunctionPointer<TransferFunc> (ValueWitnessTable->InitWithTake);

		public TypeMetadata* Metadata => &fullMetadata->Metadata;

		public ValueWitnessTable* ValueWitnessTable => fullMetadata->ValueWitnessTable;

		/// <summary>
		/// Not to be used except by <see cref="CustomViewType"/>
		/// </summary>
		internal SwiftType (FullTypeMetadata* fullMetadata)
		{
			this.fullMetadata = fullMetadata;
		}

		public SwiftType (IntPtr typeMetadata, Type? managedType = null)
		{
			this.fullMetadata = (FullTypeMetadata*)(typeMetadata - IntPtr.Size);

			// Assert assumed invariants..
			Debug.Assert (!ValueWitnessTable->IsNonBitwiseTakable, $"expected bitwise movable: {managedType?.Name}");
			if (managedType is null)
				return;
			checked {
				Debug.Assert (Metadata->TypeDescriptor->Name == GetSwiftTypeName (managedType), $"unexpected name: {Metadata->TypeDescriptor->Name}");
				Debug.Assert (Metadata->Kind == MetadataKind.OfType (managedType), $"unexpected kind: {Metadata->Kind}");
				Debug.Assert ((int)ValueWitnessTable->Size == Marshal.SizeOf (managedType), $"unexpected size: {ValueWitnessTable->Size}");
			}
		}

		/// <summary>
		/// Creates a new <see cref="SwiftType"/> that references a type from a native library.
		/// </summary>
		public SwiftType (NativeLib lib, string mangledName, Type? managedType = null)
			: this (lib.RequireSymbol ("$s" + mangledName + "N"), managedType)
		{
		}

		/// <summary>
		/// Creates a new <see cref="SwiftType"/> that references a type with a simple mangling from a native library.
		/// </summary>
		public SwiftType (NativeLib lib, string module, string name, SwiftTypeCode code, Type? managedType = null)
			: this (lib, Mangle (module, name, code), managedType)
		{
		}

		public SwiftType (NativeLib lib, Type managedType)
			: this (lib, managedType.Namespace, GetSwiftTypeName (managedType), GetSwiftTypeCode (managedType), managedType)
		{
		}

		/// <summary>
		/// Returns the <see cref="SwiftType"/> of the given <see cref="Type"/>.
		/// </summary>
		/// <remarks>
		/// By convention, types that are exposed to Swift must have a public static SwiftType property.
		/// </remarks>
		public static SwiftType? Of (Type type)
			=> type.GetProperty ("SwiftType", BindingFlags.Public | BindingFlags.Static)?.GetValue (null) as SwiftType;

		internal static string Mangle (string module, string name)
			=> (module == "Swift" ? "s" : module.Length + module) + name.Length + name;

		internal static string Mangle (string module, string name, SwiftTypeCode code)
			=> Mangle (module, name) + ((char) code);

		internal static string GetSwiftTypeName (Type ty)
		{
			if (ty.IsConstructedGenericType)
				ty = ty.GetGenericTypeDefinition ();
			if (ty.IsGenericTypeDefinition) {
				// strip off the "`N" from the end of the type name
				var name = ty.Name;
				return name.Substring (0, name.Length - 2);
			}
			return ty.Name;
		}

		internal static SwiftTypeCode GetSwiftTypeCode (Type ty)
		{
			if (ty.IsClass) return SwiftTypeCode.Class;
			if (ty.IsValueType) return SwiftTypeCode.Struct;
			throw new NotSupportedException (ty.FullName);
		}

		public virtual ProtocolWitnessTable* GetProtocolConformance (ProtocolDescriptor* descriptor)
		{
			if (descriptor == null)
				return null;

			return SwiftCoreLib.GetProtocolConformance (Metadata, descriptor);
		}

#if DEBUG_TOSTRING
		public override string ToString ()
			=> Metadata->ToString ();
#endif

		internal void Transfer (void* dest, void* src, TransferFunc func)
		{
			var witness = ValueWitnessTable;
			if (witness->IsNonPOD) {
				// In this case, one or more fields is a reference-counted reference,
				//  so we need to make sure the proper references are incremented
				func (dest, src, Metadata);
			} else {
				var bytes = (long)witness->Size;
				Buffer.MemoryCopy (src, dest, bytes, bytes);
			}
		}
		internal T Transfer<T> (in T src, TransferFunc func) where T : unmanaged
		{
			T result;
			if (ValueWitnessTable->IsNonPOD) {
				fixed (void* srcPtr = &src)
					Transfer (&result, srcPtr, func);
			} else {
				result = src;
			}
			return result;
		}

		internal void Destroy (void* data)
		{
			var witness = ValueWitnessTable;
			if (witness->IsNonPOD) {
				// In this case, one or more fields of `data` is a reference counted reference
				//  so we need to make sure the proper references are decremented
				if (_destroy is null)
					_destroy = Marshal.GetDelegateForFunctionPointer<DestroyFunc> (witness->Destroy);
				_destroy (data, Metadata);
			}
			// (no action needed for POD)
		}

		internal void Destroy<T> (in T data) where T : unmanaged
		{
			fixed (void* ptr = &data)
				Destroy (ptr);
		}
	}
}