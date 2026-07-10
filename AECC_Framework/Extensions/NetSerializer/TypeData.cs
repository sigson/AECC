/*
 * Copyright 2015 Tomi Valkeinen
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;

namespace NetSerializer
{
	sealed class TypeData
	{
		public TypeData(Type type, uint typeID, ITypeSerializer typeSerializer)
		{
			this.Type = type;
			this.TypeID = typeID;
			this.TypeSerializer = typeSerializer;
		}

		public Type Type { get; private set; }
		public uint TypeID { get; private set; }

		public ITypeSerializer TypeSerializer { get; private set; }

		/// <summary>
		/// Typed writer: SerializeDelegate&lt;Type&gt;.
		/// Read lazily (via a field load) from generated expressions, which makes
		/// mutually recursive type graphs work without a stub/body two-phase pass.
		/// </summary>
		public Delegate WriterDelegate;

		/// <summary>
		/// Typed reader: DeserializeDelegate&lt;Type&gt;
		/// </summary>
		public Delegate ReaderDelegate;

		public SerializeDelegate<object> WriterTrampolineDelegate;
		public DeserializeDelegate<object> ReaderTrampolineDelegate;

		public bool CanCallDirect
		{
			get
			{
				// We can call the (de)serializer delegate directly for:
				// - Value types
				// - Array types
				// - Sealed types with a static (de)serializer method, as the method will handle null
				// Other types go through the ObjectSerializer (to support polymorphism and null)

				var type = this.Type;

				if (type.IsValueType || type.IsArray)
					return true;

				if (type.IsSealed && (this.TypeSerializer is IStaticTypeSerializer))
					return true;

				return false;
			}
		}
	}
}
