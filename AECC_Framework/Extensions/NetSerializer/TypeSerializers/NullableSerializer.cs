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
	sealed class NullableSerializer : IExpressionTypeSerializer
	{
		public bool Handles(Type type)
		{
			if (!type.IsGenericType)
				return false;

			var genTypeDef = type.GetGenericTypeDefinition();

			return genTypeDef == typeof(Nullable<>);
		}

		public IEnumerable<Type> GetSubtypes(Type type)
		{
			var genArgs = type.GetGenericArguments();

			return new[] { typeof(bool), genArgs[0] };
		}

		public LambdaExpression GenerateWriterLambda(Serializer serializer, Type type)
		{
			var valueType = type.GetGenericArguments()[0];

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");
			var v = Expression.Parameter(type, "value");

			var hasValue = Expression.Property(v, "HasValue");

			var body = Expression.Block(
				Helpers.WriteValue(serializer, typeof(bool), s, st, hasValue),
				Expression.IfThen(
					hasValue,
					Helpers.WriteValue(serializer, valueType, s, st, Expression.Property(v, "Value"))));

			return Expression.Lambda(Helpers.GetWriterDelegateType(type), body, s, st, v);
		}

		public LambdaExpression GenerateReaderLambda(Serializer serializer, Type type)
		{
			var valueType = type.GetGenericArguments()[0];

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");

			var has = Expression.Variable(typeof(bool), "hasValue");

			var ctor = type.GetConstructor(new[] { valueType });

			var body = Expression.Block(
				new[] { has },
				Expression.Assign(has, Helpers.ReadValue(serializer, typeof(bool), s, st)),
				Expression.Condition(
					has,
					Expression.New(ctor, Helpers.ReadValue(serializer, valueType, s, st)),
					Expression.Default(type)));

			return Expression.Lambda(Helpers.GetReaderDelegateType(type), body, s, st);
		}
	}
}
