/*
 * Copyright 2015 Tomi Valkeinen
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace NetSerializer
{
	public interface ITypeSerializer
	{
		/// <summary>
		/// Returns if this TypeSerializer handles the given type
		/// </summary>
		bool Handles(Type type);

		/// <summary>
		/// Return types that are needed to serialize the given type
		/// </summary>
		IEnumerable<Type> GetSubtypes(Type type);
	}

	public interface IStaticTypeSerializer : ITypeSerializer
	{
		/// <summary>
		/// Get static method used to serialize the given type.
		/// Supported shapes: static void M(Stream, T) or static void M(Serializer, Stream, T)
		/// </summary>
		MethodInfo GetStaticWriter(Type type);

		/// <summary>
		/// Get static method used to deserialize the given type.
		/// Supported shapes:
		///   static void M(Stream, out T) / static void M(Serializer, Stream, out T)
		///   static T M(Stream)           / static T M(Serializer, Stream)
		/// </summary>
		MethodInfo GetStaticReader(Type type);
	}

	/// <summary>
	/// Expression-tree based serializer generator. Replaces the old IL-emitting
	/// IDynamicTypeSerializer so that the serializer works under AOT (IL2CPP / NativeAOT),
	/// where LambdaExpression.Compile() transparently falls back to the expression
	/// interpreter while System.Reflection.Emit is unavailable.
	/// </summary>
	public interface IExpressionTypeSerializer : ITypeSerializer
	{
		/// <summary>
		/// Build a lambda of type SerializeDelegate&lt;type&gt;:
		/// (Serializer serializer, Stream stream, T value) => void
		/// </summary>
		LambdaExpression GenerateWriterLambda(Serializer serializer, Type type);

		/// <summary>
		/// Build a lambda of type DeserializeDelegate&lt;type&gt;:
		/// (Serializer serializer, Stream stream) => T
		/// </summary>
		LambdaExpression GenerateReaderLambda(Serializer serializer, Type type);
	}
}
