﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Cosmos.IL2CPU {
  public class ILScanner {
    //Note: We have both HashSet and List because HashSet.Contains is much faster
    // than List.Contains. Also in the future we may remove items from the List
    // which have already been processed yet need to keep them in HashSet.
    //TODO: When we go threaded, these two should be encapselated into a single
    // class with thread safety.
    //TODO: These store the MethodBase which also have the IL for the body in memory
    // For large asms this could eat lot of RAM. Should convert this to remove
    // items from the list after they are processed but keep them in HashSet so we
    // know they are already done. Currently HashSet uses a refernece though, so we
    // need to hash on some UID instead of the refernce. Do not use strings, they are
    // super slow.
    private HashSet<MethodBase> mMethodsSet = new HashSet<MethodBase>();
    private List<MethodBase> mMethods = new List<MethodBase>();
    private HashSet<Type> mTypesSet = new HashSet<Type>();
    private HashSet<FieldInfo> mFieldsSet = new HashSet<FieldInfo>();
    protected ILReader mReader;

    // is a lot faster than calling GetConstructor than invoke
    protected ConstructorInfo[] mILOpsLo = new ConstructorInfo[256];
    protected ConstructorInfo[] mILOpsHi = new ConstructorInfo[256];

    public ILScanner(Type aAssemblerBaseOp) : this(aAssemblerBaseOp, false) {
    }

    public ILScanner(Type aAssemblerBaseOp, bool aSingleILOp) {
      mReader = new ILReader();
      if (aSingleILOp) {
        LoadILOp(aAssemblerBaseOp);
      } else {
        LoadILOps(aAssemblerBaseOp);
      }
    }

    protected void LoadILOp(Type aAssemblerBaseOp) {
      //TODO: Contructor.Invoke nearly doubles scanner time. Comment it out and
      // profiler runs about twice as fast. See if we can speed this up
      // http://blogs.msdn.com/haibo_luo/archive/2005/11/17/494009.aspx
      //TODO: Need to modify it to pass the arguments too for the contructor, then profile it and see if its much faster
      //mOps = new Func<ILOp>[0xFE1F];
      //      var xTemp = new DynamicMethod("Create_" + xAttrib.OpCode + "_Obj", typeof(ILOp), new Type[0], true);
      //      var xGen = xTemp.GetILGenerator();
      //      var xCtor = xType.GetConstructor(new Type[0]);
      //      xGen.Emit(OpCodes.Newobj, xCtor);
      //      xGen.Emit(OpCodes.Ret);
      //      mOps[(ushort)xAttrib.OpCode] = (Func<ILOp>)xTemp.CreateDelegate(typeof(Func<ILOp>));
      var xCtor = aAssemblerBaseOp.GetConstructors()[0];
      // Don't change the type in the foreach to a var, its necessary as it is now
      // to typecast it, so we can then recast to an int.
      foreach (ILOpCode.Code xCode in Enum.GetValues(typeof(ILOpCode.Code))) {
        int xCodeValue = (int)xCode;
        if (xCodeValue <= 0xFF) {
          mILOpsLo[xCodeValue] = xCtor;
        } else {
          mILOpsHi[xCodeValue & 0xFF] = xCtor;
        }
      }
    }

    protected void LoadILOps(Type aAssemblerBaseOp) {
      foreach (var xType in aAssemblerBaseOp.Assembly.GetExportedTypes()) {
        if (xType.IsSubclassOf(aAssemblerBaseOp)) {
          var xAttrib = (OpCodeAttribute)xType.GetCustomAttributes(typeof(OpCodeAttribute), false)[0];
          var xOpCode = (ushort)xAttrib.OpCode;
          var xCtor = xType.GetConstructors()[0];
          if (xOpCode <= 0xFF) {
            mILOpsLo[xOpCode] = xCtor;
          } else {
            mILOpsHi[xOpCode & 0xFF] = xCtor;
          }
        }
      }
    }

    public void Execute(MethodInfo aEntry) {
      // IL Operations implicitly require these types
      QueueType(typeof(string));
      QueueType(typeof(int));

      QueueMethod(aEntry);

      // Cannot use foreach, the list changes as we go
      for (int i = 0; i < mMethods.Count; i++) {
        ScanMethod(mMethods[i]);
      }
    }

    private void ScanMethod(MethodBase aMethodBase) {
      if ((aMethodBase.Attributes & MethodAttributes.PinvokeImpl) != 0) {
        // pinvoke methods dont have an embedded implementation
        return;
      } else if (aMethodBase.IsAbstract) {
        // abstract methods dont have an implementation
        return;
      }

      var xImplFlags = aMethodBase.GetMethodImplementationFlags();
      if ((xImplFlags & MethodImplAttributes.Native) != 0) {
        // native implementations cannot be compiled
        return;
      }

      var xOpCodes = mReader.ProcessMethod(aMethodBase);
      if (xOpCodes != null) {
        foreach (var xOpCode in xOpCodes) {
          //InstructionCount++;

          if (xOpCode is ILOpCodes.OpMethod) {
            QueueMethod(((ILOpCodes.OpMethod)xOpCode).Value);
          } else if (xOpCode is ILOpCodes.OpType) {
            QueueType(((ILOpCodes.OpType)xOpCode).Value);
          }

          ConstructorInfo xCtor;
          uint xOpCodeVal = (uint)xOpCode.OpCode;
          if (xOpCodeVal <= 0xFF) {
            xCtor = mILOpsLo[xOpCodeVal];
          } else {
            xCtor = mILOpsHi[xOpCodeVal & 0xFF];
          }
          var xILOp = (ILOp)xCtor.Invoke(new object[] { xOpCode });
          xILOp.Execute(0);
        }
      }
    }

    protected void QueueMethod(MethodBase aMethod) {
      if (!mMethodsSet.Contains(aMethod)) {
        mMethodsSet.Add(aMethod);
        mMethods.Add(aMethod);
        QueueType(aMethod.DeclaringType);
				var xMethodInfo = aMethod as MethodInfo;
				if (xMethodInfo != null) {
					QueueType(xMethodInfo.ReturnType);
				}
				foreach (var xParam in aMethod.GetParameters()) {
					QueueType(xParam.ParameterType);
				}
      }
    }

    protected void QueueStaticField(FieldInfo aFieldInfo) {
			if (!mFieldsSet.Contains(aFieldInfo)) {
				if (!aFieldInfo.IsStatic) {
					throw new Exception("Cannot queue instance fields!");
				}
				mFieldsSet.Add(aFieldInfo);
				QueueType(aFieldInfo.DeclaringType);
				QueueType(aFieldInfo.FieldType);
			}
		}

    protected void QueueType(Type aType) {
      if (aType != null) {
        if (!mTypesSet.Contains(aType)) {
          mTypesSet.Add(aType);
          QueueType(aType.BaseType);
          foreach (var xMethod in aType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
            if (xMethod.DeclaringType == aType) {
              if (xMethod.IsVirtual) {
                QueueMethod(xMethod);
              }
            }
          }
					// queue static constructor
					foreach (var xCctor in aType.GetConstructors(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
					{
						if (xCctor.DeclaringType == aType) {
							QueueMethod(xCctor);
						}
					}
        }
      }
    }

    public int MethodCount {
      get {
        return mMethods.Count;
      }
    }

  }
}
