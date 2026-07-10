/*
 * Copyright 2015 Tomi Valkeinen
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq.Expressions;

namespace NetSerializer
{
	sealed class EnumSerializer : IExpressionTypeSerializer
	{
		public bool Handles(Type type)
		{
			return type.IsEnum;
		}

		public IEnumerable<Type> GetSubtypes(Type type)
		{
			var underlyingType = Enum.GetUnderlyingType(type);

			return new[] { underlyingType };
		}

		public LambdaExpression GenerateWriterLambda(Serializer serializer, Type type)
		{
			Debug.Assert(type.IsEnum);

			var underlyingType = Enum.GetUnderlyingType(type);

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");
			var v = Expression.Parameter(type, "value");

			var body = Helpers.WriteValue(serializer, underlyingType, s, st,
				Expression.Convert(v, underlyingType));

			return Expression.Lambda(Helpers.GetWriterDelegateType(type), body, s, st, v);
		}

		public LambdaExpression GenerateReaderLambda(Serializer serializer, Type type)
		{
			Debug.Assert(type.IsEnum);

			var underlyingType = Enum.GetUnderlyingType(type);

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");

			var body = Expression.Convert(
				Helpers.ReadValue(serializer, underlyingType, s, st),
				type);

			return Expression.Lambda(Helpers.GetReaderDelegateType(type), body, s, st);
		}
	}
}
