/*
 * Copyright 2015 Tomi Valkeinen
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;

namespace NetSerializer
{
	sealed class ArraySerializer : IExpressionTypeSerializer
	{
		public bool Handles(Type type)
		{
			if (!type.IsArray)
				return false;

			if (type.GetArrayRank() != 1)
				throw new NotSupportedException(String.Format("Multi-dim arrays not supported: {0}", type.FullName));

			return true;
		}

		public IEnumerable<Type> GetSubtypes(Type type)
		{
			return new[] { typeof(uint), type.GetElementType() };
		}

		public LambdaExpression GenerateWriterLambda(Serializer serializer, Type type)
		{
			var elemType = type.GetElementType();

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");
			var arr = Expression.Parameter(type, "value");

			var uintData = serializer.GetTypeData(typeof(uint));

			var len = Expression.Variable(typeof(int), "len");
			var i = Expression.Variable(typeof(int), "i");
			var brk = Expression.Label("loopEnd");

			var writeNull = Helpers.InvokeWriter(uintData, s, st, Expression.Constant(0u));

			var writeArray = Expression.Block(
				new[] { len, i },
				Expression.Assign(len, Expression.ArrayLength(arr)),
				// write array len + 1
				Helpers.InvokeWriter(uintData, s, st,
					Expression.Convert(Expression.Add(len, Expression.Constant(1)), typeof(uint))),
				Expression.Assign(i, Expression.Constant(0)),
				Expression.Loop(
					Expression.IfThenElse(
						Expression.LessThan(i, len),
						Expression.Block(
							Helpers.WriteValue(serializer, elemType, s, st, Expression.ArrayAccess(arr, i)),
							Expression.PostIncrementAssign(i)),
						Expression.Break(brk)),
					brk));

			var body = Expression.IfThenElse(
				Expression.Equal(arr, Expression.Constant(null, type)),
				writeNull,
				writeArray);

			return Expression.Lambda(Helpers.GetWriterDelegateType(type), body, s, st, arr);
		}

		public LambdaExpression GenerateReaderLambda(Serializer serializer, Type type)
		{
			var elemType = type.GetElementType();

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");

			var uintData = serializer.GetTypeData(typeof(uint));

			var n = Expression.Variable(typeof(uint), "n");
			var count = Expression.Variable(typeof(int), "count");
			var arr = Expression.Variable(type, "arr");
			var i = Expression.Variable(typeof(int), "i");
			var brk = Expression.Label("loopEnd");

			var readArray = Expression.Block(
				Expression.Assign(count, Expression.Convert(Expression.Subtract(n, Expression.Constant(1u)), typeof(int))),
				Expression.Assign(arr, Expression.NewArrayBounds(elemType, count)),
				Expression.Assign(i, Expression.Constant(0)),
				Expression.Loop(
					Expression.IfThenElse(
						Expression.LessThan(i, count),
						Expression.Block(
							Expression.Assign(
								Expression.ArrayAccess(arr, i),
								Helpers.ReadValue(serializer, elemType, s, st)),
							Expression.PostIncrementAssign(i)),
						Expression.Break(brk)),
					brk));

			var body = Expression.Block(
				new[] { n, count, arr, i },
				Expression.Assign(n, Helpers.InvokeReader(uintData, s, st, typeof(uint))),
				Expression.IfThenElse(
					Expression.Equal(n, Expression.Constant(0u)),
					Expression.Assign(arr, Expression.Constant(null, type)),
					readArray),
				arr);

			return Expression.Lambda(Helpers.GetReaderDelegateType(type), body, s, st);
		}
	}
}
