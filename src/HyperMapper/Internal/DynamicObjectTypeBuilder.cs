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

        static readonly HashSet<Type> optimizeInliningType = new HashSet<Type>
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
            {typeof(DateTime)},
            {typeof(TimeSpan)},
            {typeof(DateTimeOffset)},
            {typeof(Guid)},

            {typeof(short?)},
            {typeof(int?)},
            {typeof(long?)},
            {typeof(ushort?)},
            {typeof(uint?)},
            {typeof(ulong?)},
            {typeof(float?)},
            {typeof(double?)},
            {typeof(bool?)},
            {typeof(byte?)},
            {typeof(sbyte?)},
            {typeof(char?)},
            {typeof(decimal?)},
            {typeof(DateTime?)},
            {typeof(TimeSpan?)},
            {typeof(DateTimeOffset?)},
            {typeof(Guid?)},
        };

        public static object BuildMapperFromType<TFrom, TTo>(Func<string, string> nameMutator)
        {
            var mappingInfo = MappingInfo.Create<TFrom, TTo>(nameMutator);
            
            //return DynamicObjectTypeBuilder.BuildMapperToAssembly<TFrom, TTo>(mappingInfo);

            return DynamicObjectTypeBuilder.BuildMapperToDynamicMethod<TFrom, TTo>(mappingInfo);
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

        internal static object BuildMapperToAssembly<TFrom, TTo>(MappingInfo<TFrom, TTo> mappingInfo)
        {
            var fromType = typeof(TFrom);
            var toType = typeof(TTo);

            if (ignoreTypes.Contains(fromType)) return null;
            if (ignoreTypes.Contains(toType)) return null;

            var typeBuilder = assembly.DefineType($"HyperMapper.Formatters.{{{SubtractFullNameRegex.Replace(fromType.Name, "").Replace(".", "_")}}}-{{{SubtractFullNameRegex.Replace(toType.Name, "").Replace(".", "_")}}}Formatter{Interlocked.Increment(ref nameSequence)}",
                TypeAttributes.Public | TypeAttributes.Sealed, null, new[] { typeof(IObjectMapper<TFrom, TTo>) });

            FieldBuilder beforeMapField = null;
            FieldBuilder afterMapField = null;
            Dictionary<MetaMemberPair<TFrom, TTo>, FieldBuilder> convertActionDictionary = null;

            var ctorConvertActions = mappingInfo.TargetMembers.Where(x => x.ConvertAction != null).ToArray();

            // ctor(Action<TFrom> beforeMap, Action<TTo> afterMap, convertActions, ...,);
            {
                var method = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(Action<TFrom>), typeof(Action<TTo>) }.Concat(ctorConvertActions.Select(_ => typeof(object))).ToArray());
                var il = method.GetILGenerator();
                beforeMapField = typeBuilder.DefineField("beforeMap", typeof(Action<TFrom>), FieldAttributes.Private | FieldAttributes.InitOnly);
                afterMapField = typeBuilder.DefineField("afterMap", typeof(Action<TTo>), FieldAttributes.Private | FieldAttributes.InitOnly);

                BuildConstructor(il, mappingInfo, beforeMapField, afterMapField, typeBuilder, ctorConvertActions, out convertActionDictionary);
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
                }, convertActionDictionary);
            }

            var typeInfo = typeBuilder.CreateTypeInfo();
            return Activator.CreateInstance(typeInfo, new object[] { mappingInfo.BeforeMap, mappingInfo.AfterMap }.Concat(ctorConvertActions).ToArray());
        }

        internal static object BuildMapperToDynamicMethod<TFrom, TTo>(MappingInfo<TFrom, TTo> mappingInfo)
        {
            var fromType = typeof(TFrom);
            var toType = typeof(TTo);

            if (ignoreTypes.Contains(fromType)) return null;
            if (ignoreTypes.Contains(toType)) return null;

            // TTo Map(TFrom from, IObjectMapperResolver resolver);
            {
                // TODo:beforeAction, toAction...

                var method = new DynamicMethod("Map", toType, new[] { typeof(object[]), fromType, typeof(IObjectMapperResolver) }, fromType.Module, true);
                var il = method.GetILGenerator();
                BuildMap(il, mappingInfo, 1, null, null, new Dictionary<MetaMemberPair<TFrom, TTo>, FieldBuilder>()); // TODO:convert fields...

                var delgate = method.CreateDelegate(typeof(Func<,,,>).MakeGenericType(typeof(object[]), fromType, typeof(IObjectMapperResolver), toType));

                // TODO:converts
                return Activator.CreateInstance(typeof(DynamicMethodMapper<,>).MakeGenericType(fromType, toType), new object[] { null, delgate });
            }
        }

        static void BuildConstructor<TFrom, TTo>(ILGenerator il, MappingInfo<TFrom, TTo> mappingInfo, FieldBuilder beforeMapField, FieldBuilder afterMapField, TypeBuilder typeBuilder, MetaMemberPair<TFrom, TTo>[] convertActions, out Dictionary<MetaMemberPair<TFrom, TTo>, FieldBuilder> convertActionDictionary)
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

            convertActionDictionary = new Dictionary<MetaMemberPair<TFrom, TTo>, FieldBuilder>(convertActions.Length);
            var loadIndex = 3;
            foreach (var item in convertActions)
            {
                var field = typeBuilder.DefineField("convertAction" + loadIndex, typeof(object), FieldAttributes.Private | FieldAttributes.InitOnly);

                il.EmitLoadThis();
                il.EmitLdarg(loadIndex++);
                il.Emit(OpCodes.Stfld, field);

                convertActionDictionary.Add(item, field);
            }

            il.Emit(OpCodes.Ret);
        }

        static void BuildMap<TFrom, TTo>(ILGenerator il, MappingInfo<TFrom, TTo> mappingInfo, int firstArgIndex, Action emitBeforeMapLoadDelegate, Action emitAfterMapLoadDelegate, Dictionary<MetaMemberPair<TFrom, TTo>, FieldBuilder> convertActionDictionary)
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

            // if(from == null) return null
            if (!fromType.IsValueType)
            {
                var gotoNextLabel = il.DefineLabel();
                argFrom.EmitLoad();
                il.Emit(OpCodes.Brtrue_S, gotoNextLabel);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(gotoNextLabel);
            }

            // construct totype
            var result = EmitNewObject<TFrom, TTo>(il, argFrom, mappingInfo.TargetConstructor);

            // map from -> to.

            foreach (var item in mappingInfo.TargetMembers)
            {
                EmitMapMember(il, item, argFrom, argResolver, result, convertActionDictionary);
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

        static void EmitMapMember<TFrom, TTo>(ILGenerator il, MetaMemberPair<TFrom, TTo> pair, ArgumentField argFrom, ArgumentField argResolver, LocalBuilder toLocal, Dictionary<MetaMemberPair<TFrom, TTo>, FieldBuilder> convertFields)
        {
            if (pair.From == null)
            {
                // To and conversion pattern(Func<TFrom, TFromMember>).
                if (toLocal.LocalType.IsValueType)
                {
                    il.EmitLdloca(toLocal);
                }
                else
                {
                    il.EmitLdloc(toLocal);
                }

                // note: if use DynamicMethod, should change field to argument.

                // Func[TFrom, TToMember]
                var convertField = convertFields[pair];
                il.EmitLoadThis();
                il.EmitLdfld(convertField);
                il.Emit(OpCodes.Castclass, typeof(Func<,>).MakeGenericType(typeof(TFrom), pair.To.Type));
                argFrom.EmitLdarg();
                il.EmitCall(EmitInfo.GetFuncInvokeDynamic(typeof(TFrom), pair.To.Type));
                pair.To.EmitStoreValue(il);
            }
            else
            {
                // optimize for primitive to primitive
                if (pair.From.Type == pair.To.Type)
                {
                    if (optimizeInliningType.Contains(pair.To.Type))
                    {
                        if (toLocal.LocalType.IsValueType)
                        {
                            il.EmitLdloca(toLocal);
                        }
                        else
                        {
                            il.EmitLdloc(toLocal);
                        }

                        if (convertFields.TryGetValue(pair, out var convertField))
                        {
                            il.EmitLoadThis();
                            il.EmitLdfld(convertField);
                            il.Emit(OpCodes.Castclass, typeof(Func<,>).MakeGenericType(pair.From.Type, pair.To.Type));
                        }

                        argFrom.EmitLoad();
                        pair.From.EmitLoadValue(il);

                        if (convertField != null)
                        {
                            il.EmitCall(EmitInfo.GetFuncInvokeDynamic(pair.From.Type, pair.To.Type));
                        }

                        pair.To.EmitStoreValue(il);
                        return;
                    }

                    // more aggressive inlining for primitive[]
                    if (pair.To.Type.IsArray)
                    {
                        var elemType = pair.To.Type.GetElementType();
                        var mapMethodInfo = EmitInfo.GetMemoryCopyMapperMap(elemType);

                        if (mapMethodInfo != null)
                        {
                            il.EmitLdloc(toLocal);

                            if (convertFields.TryGetValue(pair, out var convertField))
                            {
                                il.EmitLoadThis();
                                il.EmitLdfld(convertField);
                                il.Emit(OpCodes.Castclass, typeof(Func<,>).MakeGenericType(pair.From.Type, pair.To.Type));
                            }

                            argFrom.EmitLoad();
                            pair.From.EmitLoadValue(il);
                            il.EmitCall(mapMethodInfo);

                            if (convertField != null)
                            {
                                il.EmitCall(EmitInfo.GetFuncInvokeDynamic(pair.From.Type, pair.To.Type));
                            }

                            pair.To.EmitStoreValue(il);
                            return;
                        }
                    }
                }

                // standard mapping(Mapper.Map)
                {
                    if (toLocal.LocalType.IsValueType)
                    {
                        il.EmitLdloca(toLocal);
                    }
                    else
                    {
                        il.EmitLdloc(toLocal);
                    }

                    if (convertFields.TryGetValue(pair, out var convertField))
                    {
                        il.EmitLoadThis();
                        il.EmitLdfld(convertField);
                        il.Emit(OpCodes.Castclass, typeof(Func<,>).MakeGenericType(pair.From.Type, pair.To.Type));
                    }

                    argResolver.EmitLoad();
                    il.EmitCall(EmitInfo.GetGetMapperWithVerifyDynamic(pair.From.Type, pair.To.Type));
                    argFrom.EmitLoad();
                    pair.From.EmitLoadValue(il);
                    argResolver.EmitLoad();
                    il.EmitCall(EmitInfo.GetMapDynamic(pair.From.Type, pair.To.Type));

                    if (convertField != null)
                    {
                        il.EmitCall(EmitInfo.GetFuncInvokeDynamic(pair.From.Type, pair.To.Type));
                    }

                    pair.To.EmitStoreValue(il);
                }
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

            public static MethodInfo GetFuncInvoke<T1, T2>() => ExpressionUtility.GetMethodInfo((Func<T1, T2> f) => f.Invoke(default(T1)));
            public static MethodInfo GetFuncInvokeDynamic(Type t1, Type t2) => typeof(Func<,>).MakeGenericType(t1, t2).GetMethod("Invoke");

            public static MethodInfo GetMemoryCopyMapperMap(Type elementType)
            {
                switch (Type.GetTypeCode(elementType))
                {
                    case TypeCode.Boolean:
                        return ExpressionUtility.GetMethodInfo(() => BooleanMemoryCopyMapper.Map(null));
                    case TypeCode.Char:
                        return ExpressionUtility.GetMethodInfo(() => CharMemoryCopyMapper.Map(null));
                    case TypeCode.SByte:
                        return ExpressionUtility.GetMethodInfo(() => SByteMemoryCopyMapper.Map(null));
                    case TypeCode.Byte:
                        return ExpressionUtility.GetMethodInfo(() => ByteMemoryCopyMapper.Map(null));
                    case TypeCode.Int16:
                        return ExpressionUtility.GetMethodInfo(() => Int16MemoryCopyMapper.Map(null));
                    case TypeCode.UInt16:
                        return ExpressionUtility.GetMethodInfo(() => UInt16MemoryCopyMapper.Map(null));
                    case TypeCode.Int32:
                        return ExpressionUtility.GetMethodInfo(() => Int32MemoryCopyMapper.Map(null));
                    case TypeCode.UInt32:
                        return ExpressionUtility.GetMethodInfo(() => UInt32MemoryCopyMapper.Map(null));
                    case TypeCode.Int64:
                        return ExpressionUtility.GetMethodInfo(() => Int64MemoryCopyMapper.Map(null));
                    case TypeCode.UInt64:
                        return ExpressionUtility.GetMethodInfo(() => UInt64MemoryCopyMapper.Map(null));
                    case TypeCode.Single:
                        return ExpressionUtility.GetMethodInfo(() => SingleMemoryCopyMapper.Map(null));
                    case TypeCode.Double:
                        return ExpressionUtility.GetMethodInfo(() => DoubleMemoryCopyMapper.Map(null));
                    case TypeCode.Decimal:
                        return ExpressionUtility.GetMethodInfo(() => DecimalMemoryCopyMapper.Map(null));
                    default:
                        return null;
                }

            }
        }

        internal sealed class DynamicMethodWithActionMapper<TFrom, TTo> : IObjectMapper<TFrom, TTo>
        {
            readonly Action<TFrom> fromAction;
            readonly Action<TTo> toAction;
            readonly object[] converts;
            readonly Func<object[], TFrom, IObjectMapperResolver, TTo> map;

            public DynamicMethodWithActionMapper(Action<TFrom> fromAction, Action<TTo> toAction, object[] converts, Func<object[], TFrom, IObjectMapperResolver, TTo> map)
            {
                this.fromAction = fromAction;
                this.toAction = toAction;
                this.converts = converts;
                this.map = map;
            }

            public TTo Map(TFrom from, IObjectMapperResolver resolver)
            {
                if (fromAction != null)
                {
                    fromAction(from);
                }

                var result = map(converts, from, resolver);

                if (toAction != null)
                {
                    toAction(result);
                }

                return result;
            }
        }

        internal sealed class DynamicMethodMapper<TFrom, TTo> : IObjectMapper<TFrom, TTo>
        {
            readonly object[] converts;
            readonly Func<object[], TFrom, IObjectMapperResolver, TTo> map;

            public DynamicMethodMapper(object[] converts, Func<object[], TFrom, IObjectMapperResolver, TTo> map)
            {
                this.converts = converts;
                this.map = map;
            }

            public TTo Map(TFrom from, IObjectMapperResolver resolver)
            {
                return map(converts, from, resolver);
            }
        }
    }
}