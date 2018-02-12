﻿using HyperMapper.Internal.Emit;
using HyperMapper.Mappers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Threading;

namespace HyperMapper.Internal
{
#if DEBUG && (NET45 || NET47)
    public

#else
    internal
#endif
        static class DynamicObjectTypeBuilder
    {
        const string ModuleName = "HyperMapper.AdhocMappers";

        internal static readonly DynamicAssembly assembly;

        static DynamicObjectTypeBuilder()
        {
            assembly = new DynamicAssembly(ModuleName);
        }

#if DEBUG && (NET45 || NET47)
        public static AssemblyBuilder Save()
        {
            return assembly.Save();
        }
#endif

        static readonly Regex SubtractFullNameRegex = new Regex(@", Version=\d+.\d+.\d+.\d+, Culture=\w+, PublicKeyToken=\w+", RegexOptions.Compiled);
        static int nameSequence = 0;

        static readonly HashSet<Type> ignoreTypes = new HashSet<Type>
        {
            {typeof(object)},
            {typeof(short)},
            {typeof(int)},
            {typeof(long)},
            {typeof(ushort)},
            {typeof(uint)},
            {typeof(ulong)},
            {typeof(float)},
            {typeof(double)},
            {typeof(bool)},
            {typeof(byte)},
            {typeof(sbyte)},
            {typeof(decimal)},
            {typeof(char)},
            {typeof(string)},
            {typeof(System.Guid)},
            {typeof(System.TimeSpan)},
            {typeof(System.DateTime)},
            {typeof(System.DateTimeOffset)},
        };

        static readonly HashSet<Type> primitiveTypes = new HashSet<Type>
        {
            {typeof(short)},
            {typeof(int)},
            {typeof(long)},
            {typeof(ushort)},
            {typeof(uint)},
            {typeof(ulong)},
            {typeof(float)},
            {typeof(double)},
            {typeof(bool)},
            {typeof(byte)},
            {typeof(sbyte)},
            {typeof(char)},
            {typeof(decimal)},
            {typeof(string)},
        };

        // TODO:...
        public static object BuildMapperFromType<TFrom, TTo>(Func<string, string> nameMutator)
        {
            var nullableMapper = HandleNullable(typeof(TFrom), typeof(TTo));
            if (nullableMapper != null) return nullableMapper;

            // TODO:AnonymousType
            //Type elementType;
            //if (ti.IsAnonymous() || TryGetInterfaceEnumerableElementType(typeof(T), out elementType))
            //{
            //    return DynamicObjectTypeBuilder.BuildAnonymousFormatter(typeof(T), nameMutator, excludeNull, false);
            //}

            var mappingInfo = MappingInfo.Create<TFrom, TTo>(nameMutator);
            return DynamicObjectTypeBuilder.BuildMapper<TFrom, TTo>(mappingInfo);
        }

        static object HandleNullable(Type from, Type to)
        {
            var isFromNullable = from.IsNullable();
            var isToNullable = to.IsNullable();

            if (isFromNullable && isToNullable)
            {
                // unwrap nullable type
                return Activator.CreateInstance(typeof(NullableMapperFromNullableStructToNullableStruct<,>).MakeGenericType(from.GenericTypeArguments[0], to.GenericTypeArguments[0]));
            }
            else if (isFromNullable)
            {
                if (to.IsValueType)
                {
                    return Activator.CreateInstance(typeof(NullableMapperFromNullableStructToStruct<,>).MakeGenericType(from.GenericTypeArguments[0], to));
                }
                else
                {
                    return Activator.CreateInstance(typeof(NullableMapperFromNullableStructToClass<,>).MakeGenericType(from.GenericTypeArguments[0], to));
                }
            }
            else if (isToNullable)
            {
                if (from.IsValueType)
                {
                    return Activator.CreateInstance(typeof(NullableMapperFromStructToNullableStruct<,>).MakeGenericType(from, to.GenericTypeArguments[0]));
                }
                else
                {
                    return Activator.CreateInstance(typeof(NullableMapperFromClassToNullableStruct<,>).MakeGenericType(from, to.GenericTypeArguments[0]));
                }
            }

            return null;
        }

        internal static object BuildMapper<TFrom, TTo>(MappingInfo<TFrom, TTo> mappingInfo)
        {
            var fromType = typeof(TFrom);
            var toType = typeof(TTo);

            if (ignoreTypes.Contains(fromType)) return null;
            if (ignoreTypes.Contains(toType)) return null;

            var typeBuilder = assembly.DefineType($"HyperMapper.Formatters.{{{SubtractFullNameRegex.Replace(fromType.FullName, "").Replace(".", "_")}}}-{{{SubtractFullNameRegex.Replace(toType.FullName, "").Replace(".", "_")}}}Formatter{Interlocked.Increment(ref nameSequence)}",
                TypeAttributes.Public | TypeAttributes.Sealed, null, new[] { typeof(IObjectMapper<TFrom, TTo>) });

            FieldBuilder beforeMapField = null;
            FieldBuilder afterMapField = null;

            // ctor(Action<TFrom> beforeMap, Action<TTo> afterMap);
            {
                var method = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(Action<TFrom>), typeof(Action<TTo>) });
                var il = method.GetILGenerator();
                beforeMapField = typeBuilder.DefineField("beforeMap", typeof(Action<TFrom>), FieldAttributes.Private | FieldAttributes.InitOnly);
                afterMapField = typeBuilder.DefineField("afterMap", typeof(Action<TTo>), FieldAttributes.Private | FieldAttributes.InitOnly);

                BuildConstructor(il, mappingInfo, beforeMapField, afterMapField);
            }

            // TTo Map(TFrom from, IObjectMapperResolver resolver);
            {
                var method = typeBuilder.DefineMethod("Map", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                    toType,
                    new Type[] { fromType, typeof(IObjectMapperResolver) });
                var il = method.GetILGenerator();
                BuildMap(il, mappingInfo, 1, () =>
                {
                    il.EmitLoadThis();
                    il.EmitLdfld(beforeMapField);
                }, () =>
                {
                    il.EmitLoadThis();
                    il.EmitLdfld(afterMapField);
                }, () =>
                {
                });
            }

            var typeInfo = typeBuilder.CreateTypeInfo();
            return Activator.CreateInstance(typeInfo, new object[] { mappingInfo.BeforeMap, mappingInfo.AfterMap });
        }

        static void BuildConstructor<TFrom, TTo>(ILGenerator il, MappingInfo<TFrom, TTo> mappingInfo, FieldBuilder beforeMapField, FieldBuilder afterMapField)
        {
            il.EmitLdarg(0);
            il.Emit(OpCodes.Call, EmitInfo.ObjectCtor);

            if (mappingInfo.BeforeMap != null)
            {
                il.EmitLoadThis();
                il.EmitLdarg(1); // beforeMap
                il.Emit(OpCodes.Stfld, beforeMapField);
            }

            if (mappingInfo.AfterMap != null)
            {
                il.EmitLoadThis();
                il.EmitLdarg(2); // afterMap
                il.Emit(OpCodes.Stfld, afterMapField);
            }

            il.Emit(OpCodes.Ret);
        }

        static void BuildMap<TFrom, TTo>(ILGenerator il, MappingInfo<TFrom, TTo> mappingInfo, int firstArgIndex, Action emitBeforeMapLoadDelegate, Action emitAfterMapLoadDelegate, Action emitLoadConvertAction)
        {
            var fromType = typeof(TFrom);
            var toType = typeof(TTo);

            var argFrom = new ArgumentField(il, firstArgIndex, fromType);
            var argResolver = new ArgumentField(il, firstArgIndex + 1);

            // call beforeMap
            if (mappingInfo.BeforeMap != null)
            {
                emitBeforeMapLoadDelegate();
                argFrom.EmitLdarg();
                il.EmitCall(EmitInfo.GetActionInvoke<TFrom>());
            }

            // construct totype
            var result = EmitNewObject<TFrom, TTo>(il, argFrom, mappingInfo.TargetConstructor);

            // map from -> to.
            foreach (var item in mappingInfo.TargetMembers)
            {
                EmitMapMember(il, item, argFrom, argResolver, result);
            }

            // call afterMap
            if (mappingInfo.AfterMap != null)
            {
                emitAfterMapLoadDelegate();
                il.EmitLdloc(result);
                il.EmitCall(EmitInfo.GetActionInvoke<TTo>());
            }

            // end.
            il.EmitLdloc(result);
            il.Emit(OpCodes.Ret);
        }

        static LocalBuilder EmitNewObject<TFrom, TTo>(ILGenerator il, ArgumentField fromValue, MetaConstructorInfo<TFrom, TTo> metaConstructorInfo)
        {
            var toType = typeof(TTo);
            var result = il.DeclareLocal(toType);

            if (toType.IsClass)
            {
                if (metaConstructorInfo == null || metaConstructorInfo.ConstructorInfo == null)
                {
                    throw new InvalidOperationException("ConstructorInfo does not find, " + typeof(TFrom) + " -> " + typeof(TTo) + " Mapper.");
                }

                foreach (var item in metaConstructorInfo.Arguments)
                {
                    fromValue.EmitLoad();
                    item.EmitLoadValue(il);
                }
                il.Emit(OpCodes.Newobj, metaConstructorInfo.ConstructorInfo);
                il.EmitStloc(result);
            }
            else
            {
                if (metaConstructorInfo == null || metaConstructorInfo.ConstructorInfo == null)
                {
                    il.Emit(OpCodes.Ldloca, result);
                    il.Emit(OpCodes.Initobj, toType);
                }
                else
                {
                    foreach (var item in metaConstructorInfo.Arguments)
                    {
                        fromValue.EmitLoad();
                        item.EmitLoadValue(il);
                    }
                    il.Emit(OpCodes.Newobj, metaConstructorInfo.ConstructorInfo);
                    il.EmitStloc(result);
                }
            }

            return result;
        }

        static void EmitMapMember<TFrom, TTo>(ILGenerator il, MetaMemberPair<TFrom, TTo> pair, ArgumentField argFrom, ArgumentField argResolver, LocalBuilder toLocal)
        {
            // var member = info.MemberInfo;
            var toType = typeof(TTo);

            if (primitiveTypes.Contains(pair.To.Type))
            {
                // TODO:ConvertAction
                if (toLocal.LocalType.IsValueType)
                {
                    il.EmitLdloca(toLocal);
                }
                else
                {
                    il.EmitLdloc(toLocal);
                }
                argFrom.EmitLoad();
                pair.From.EmitLoadValue(il);
                pair.To.EmitStoreValue(il);
            }
            else
            {
                // TODO:ConvertAction
                if (toLocal.LocalType.IsValueType)
                {
                    il.EmitLdloca(toLocal);
                }
                else
                {
                    il.EmitLdloc(toLocal);
                }
                argResolver.EmitLoad();
                il.EmitCall(EmitInfo.GetGetMapperWithVerifyDynamic(pair.From.Type, pair.To.Type));
                argFrom.EmitLoad();
                pair.From.EmitLoadValue(il);
                argResolver.EmitLoad();
                il.EmitCall(EmitInfo.GetMapDynamic(pair.From.Type, pair.To.Type));
                pair.To.EmitStoreValue(il);
            }
        }

        static class EmitInfo
        {
            public static readonly ConstructorInfo ObjectCtor = typeof(object).GetTypeInfo().DeclaredConstructors.First(x => x.GetParameters().Length == 0);

            public static MethodInfo GetActionInvoke<T>() => ExpressionUtility.GetMethodInfo((Action<T> act) => act.Invoke(default(T)));
            public static MethodInfo GetGetMapperWithVerify<TFrom, TTo>() => ExpressionUtility.GetMethodInfo((IObjectMapperResolver resolver) => resolver.GetMapperWithVerify<TFrom, TTo>());
            public static MethodInfo GetGetMapperWithVerifyDynamic(Type tfrom, Type tTo) => typeof(ObjectMapperResolverExtensions).GetMethod(nameof(ObjectMapperResolverExtensions.GetMapperWithVerify)).MakeGenericMethod(tfrom, tTo);
            public static MethodInfo GetMap<TFrom, TTo>() => ExpressionUtility.GetMethodInfo((IObjectMapper<TFrom, TTo> mapper) => mapper.Map(default(TFrom), default(IObjectMapperResolver)));
            public static MethodInfo GetMapDynamic(Type tfrom, Type tTo) => typeof(IObjectMapper<,>).MakeGenericType(tfrom, tTo).GetMethod("Map");
        }
    }
}