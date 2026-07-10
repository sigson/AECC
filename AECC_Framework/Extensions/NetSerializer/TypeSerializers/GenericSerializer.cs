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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;

namespace NetSerializer
{
	sealed class GenericSerializer : IExpressionTypeSerializer
	{
		static readonly MethodInfo s_getUninitializedObject =
			typeof(FormatterServices).GetMethod(nameof(FormatterServices.GetUninitializedObject),
				BindingFlags.Public | BindingFlags.Static);

		static readonly MethodInfo s_fieldSetValue =
			typeof(FieldInfo).GetMethod(nameof(FieldInfo.SetValue), new[] { typeof(object), typeof(object) });

		public bool Handles(Type type)
		{
			if (!type.IsSerializable)
				throw new NotSupportedException(String.Format("Type {0} is not marked as Serializable", type.FullName));

			if (typeof(ISerializable).IsAssignableFrom(type))
				throw new NotSupportedException(String.Format("Cannot serialize {0}: ISerializable not supported", type.FullName));

			return true;
		}

		public IEnumerable<Type> GetSubtypes(Type type)
		{
			var fields = Helpers.GetFieldInfos(type);

			foreach (var field in fields)
				yield return field.FieldType;
		}

		static IEnumerable<MethodInfo> GetMethodsWithAttributes(Type type, Type attrType)
		{
			var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

			var methods = type.GetMethods(flags)
				.Where(m => m.GetCustomAttributes(attrType, false).Any());

			if (type.BaseType == null)
			{
				return methods;
			}
			else
			{
				var baseMethods = GetMethodsWithAttributes(type.BaseType, attrType);
				return baseMethods.Concat(methods);
			}
		}

		static void AppendCallbackCalls(Type type, Expression instance, Type attrType, List<Expression> exprs)
		{
			foreach (var m in GetMethodsWithAttributes(type, attrType))
			{
				if (type.IsValueType)
					throw new NotImplementedException("Serialization callbacks not supported for Value types");

				exprs.Add(Expression.Call(instance, m, Expression.Default(typeof(StreamingContext))));
			}
		}

		public LambdaExpression GenerateWriterLambda(Serializer serializer, Type type)
		{
			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");
			var v = Expression.Parameter(type, "value");

			var exprs = new List<Expression>();

			if (serializer.Settings.SupportSerializationCallbacks)
				AppendCallbackCalls(type, v, typeof(OnSerializingAttribute), exprs);

			foreach (var field in Helpers.GetFieldInfos(type))
			{
				exprs.Add(Helpers.WriteValue(serializer, field.FieldType, s, st,
					Expression.Field(v, field)));
			}

			if (serializer.Settings.SupportSerializationCallbacks)
				AppendCallbackCalls(type, v, typeof(OnSerializedAttribute), exprs);

			Expression body = exprs.Count > 0 ? (Expression)Expression.Block(exprs) : Expression.Empty();

			return Expression.Lambda(Helpers.GetWriterDelegateType(type), body, s, st, v);
		}

		public LambdaExpression GenerateReaderLambda(Serializer serializer, Type type)
		{
			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");

			var fields = Helpers.GetFieldInfos(type).ToList();

			Expression body;

			if (type.IsValueType)
				body = GenerateStructReaderBody(serializer, type, fields, s, st);
			else
				body = GenerateClassReaderBody(serializer, type, fields, s, st);

			return Expression.Lambda(Helpers.GetReaderDelegateType(type), body, s, st);
		}

		Expression GenerateClassReaderBody(Serializer serializer, Type type, List<FieldInfo> fields,
			ParameterExpression s, ParameterExpression st)
		{
			var inst = Expression.Variable(type, "instance");

			var exprs = new List<Expression>();

			// instance = (T)FormatterServices.GetUninitializedObject(typeof(T))
			exprs.Add(Expression.Assign(inst,
				Expression.Convert(
					Expression.Call(s_getUninitializedObject, Expression.Constant(type, typeof(Type))),
					type)));

			if (serializer.Settings.SupportSerializationCallbacks)
				AppendCallbackCalls(type, inst, typeof(OnDeserializingAttribute), exprs);

			foreach (var field in fields)
				exprs.Add(BuildFieldAssign(serializer, inst, field, s, st));

			if (serializer.Settings.SupportSerializationCallbacks)
				AppendCallbackCalls(type, inst, typeof(OnDeserializedAttribute), exprs);

			if (serializer.Settings.SupportIDeserializationCallback &&
				typeof(IDeserializationCallback).IsAssignableFrom(type))
			{
				var mi = typeof(IDeserializationCallback).GetMethod(nameof(IDeserializationCallback.OnDeserialization),
					new[] { typeof(object) });

				exprs.Add(Expression.Call(inst, mi, Expression.Constant(null, typeof(object))));
			}

			exprs.Add(inst);

			return Expression.Block(new[] { inst }, exprs);
		}

		Expression GenerateStructReaderBody(Serializer serializer, Type type, List<FieldInfo> fields,
			ParameterExpression s, ParameterExpression st)
		{
			bool hasInitOnly = fields.Any(f => f.IsInitOnly);

			if (!hasInitOnly)
			{
				// var v = default(T); assign fields; return v
				var inst = Expression.Variable(type, "instance");

				var exprs = new List<Expression>();

				foreach (var field in fields)
					exprs.Add(Expression.Assign(Expression.Field(inst, field),
						Helpers.ReadValue(serializer, field.FieldType, s, st)));

				exprs.Add(inst);

				return Expression.Block(new[] { inst }, exprs);
			}
			else
			{
				// A struct with initonly (readonly / getter-only auto-property) fields:
				// mutate a boxed copy via FieldInfo.SetValue, then unbox.
				var box = Expression.Variable(typeof(object), "box");

				var exprs = new List<Expression>();

				exprs.Add(Expression.Assign(box,
					Expression.Call(s_getUninitializedObject, Expression.Constant(type, typeof(Type)))));

				foreach (var field in fields)
				{
					exprs.Add(Expression.Call(
						Expression.Constant(field, typeof(FieldInfo)),
						s_fieldSetValue,
						box,
						Expression.Convert(Helpers.ReadValue(serializer, field.FieldType, s, st), typeof(object))));
				}

				exprs.Add(Expression.Unbox(box, type));

				return Expression.Block(new[] { box }, exprs);
			}
		}

		Expression BuildFieldAssign(Serializer serializer, Expression inst, FieldInfo field,
			ParameterExpression s, ParameterExpression st)
		{
			var value = Helpers.ReadValue(serializer, field.FieldType, s, st);

			if (!field.IsInitOnly)
				return Expression.Assign(Expression.Field(inst, field), value);

			// readonly field: Expression.Assign rejects initonly fields, fall back to reflection
			return Expression.Call(
				Expression.Constant(field, typeof(FieldInfo)),
				s_fieldSetValue,
				Expression.Convert(inst, typeof(object)),
				Expression.Convert(value, typeof(object)));
		}
	}
}
