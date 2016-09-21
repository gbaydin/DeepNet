﻿namespace SymTensor.Compiler.Cuda

open System
open System.Runtime.InteropServices
open ManagedCuda
open ManagedCuda.BasicTypes

open Basics
open Basics.Cuda
open ArrayNDNS
open SymTensor
open SymTensor.Compiler
open UExprTypes


#nowarn "9"

[<AutoOpen>]
module Types =

    /// device memory pointer
    type DevMemPtrT = {
        /// base memory
        Base: MemManikinT;
        /// offset in elements
        Offset: int
    }

    /// pre-allocated host memory 
    type HostExternalMemT = {Name: string}
    /// host memory pointer
    type HostMemPtrT = {
        Base: HostExternalMemT;
        Offset: int
    }

    /// a CUDA texture object
    type TextureObjectT = {
        Contents:                  ArrayNDManikinT
        Descriptor:                BasicTypes.CudaTextureDescriptor
    }

    /// additional environment informations for CUDA
    type CudaCompileEnvT = {
        /// storage location of variables
        VarStorLoc:                 Map<UVarSpecT, ArrayLocT>
        /// op names for each elements function
        mutable ElemFuncsOpNames:   Map<UElemExpr.UElemFuncT, string>
        /// texture objects
        TextureObjects:             ResizeArray<TextureObjectT>
        /// textures for interpolator
        InterpolatorTextures:       Dictionary<IInterpolator, TextureObjectT>
        /// values for constants
        ConstantValues:             Dictionary<MemConstManikinT, IArrayNDCudaT>
    }

    /// function domain (kernel only or host code that may call kernels)
    type FuncDomainT =
        | KernelFunc
        | CPPFunc

    /// template instantiation specification
    type TmplInstT = {
        FuncName:       string; 
        Domain:         FuncDomainT; 
        TmplArgs:       string list; 
        RetType:        string; 
        ArgTypes:       string list;
    }

    /// a CUDA compute stream
    type StreamT = int

    /// CUDA event object
    type EventObjectT = int

    /// a CUDA event that can be used for synchronization
    type EventT = {
        EventObjectId: int; 
        CorrelationId: int;     
        EmittingExecUnitId: int;
    }

    type RegHostMemT = {
        Ptr:                    nativeint
        CudaRegHostMem:         CudaRegisteredHostMemory<byte>
    }

    type CudaTexObjectAndArray = {
        TexObject:              CudaTexObject
        TexArray:               IDisposable
    }

    /// Actual CUDA internal memory allocations and external device and host references
    type CudaExecEnvT = {
        Stream:                 Dictionary<StreamT, CudaStream>
        Event:                  Dictionary<EventObjectT, CudaEvent>
        InternalMem:            Dictionary<MemAllocManikinT, CudaDeviceVariable<byte>>
        RegHostMem:             Dictionary<MemAllocManikinT, RegHostMemT>
        mutable ExternalVar:    Map<UVarSpecT, IArrayNDCudaT>
        mutable HostVar:        Map<UVarSpecT, IArrayNDHostT>
        TextureObject:          Dictionary<TextureObjectT, CudaTexObjectAndArray>
        ConstantValues:         Map<MemConstManikinT, IArrayNDCudaT>
    }
    
    /// CUDA device memory range
    type DevMemRngT = {
        DeviceMem:              CudaDeviceVariable<byte>;
        OffsetInBytes:          int;
        LengthInBytes:          int;
    }

    /// CUDA host memory range
    type HostMemRngT = {
        HostMem:                CudaRegisteredHostMemory<byte>;
        OffsetInBytes:          int;
        LengthInBytes:          int;
    }


    /// BLAS transpose operation
    type BlasTransposeOpT =
        | BlasId
        | BlasTranspose

        member this.CudaBlasOperation =
            match this with
            | BlasId -> CudaBlas.Operation.NonTranspose
            | BlasTranspose -> CudaBlas.Operation.Transpose

    /// specifies the name of the C++ function
    type CPPFuncNameAttribute (cppFuncName: string) =
        inherit System.Attribute()     
        member this.CPPFuncName = cppFuncName


module CudaCompileEnv =

    /// creates a new texture object
    let newTextureObject (env: CudaCompileEnvT) contents descriptor =
        if not (ArrayND.isC contents && contents.Layout.Offset = 0) then
            failwith "manikin for use with texture must be contiguous and offset free"
        let texObj = {
            Contents   = contents
            Descriptor = descriptor
        }
        env.TextureObjects.Add texObj
        texObj

    let newConstant (env: CudaCompileEnvT) (value: IArrayNDCudaT) =   
        let mc : MemConstManikinT = {
            Id = env.ConstantValues.Keys.Count
            TypeName = TypeName.ofTypeInst value.DataType
        }
        env.ConstantValues.Add (mc, value)
        ArrayNDManikinT (value.Layout, MemConst mc)


module CudaExecEnv = 

    /// Gets allocated host memory.
    let getHostRegMem (env: CudaExecEnvT) (memManikin: MemManikinT) = 
        match memManikin with
        | MemAlloc im -> env.RegHostMem.[im]
        | _ -> failwithf "memory manikin %A was supposed to be an allocation" memManikin

    /// Gets device memory and offset in bytes for an internal allocation or external reference.
    let getDevMem (env: CudaExecEnvT) (memManikin: MemManikinT) =
        match memManikin with
        | MemAlloc im -> env.InternalMem.[im], 0
        | MemExternal vs ->
            let ev = env.ExternalVar.[vs]
            if ArrayND.isC ev then 
                ev.Storage.ByteData, (ArrayND.offset ev) * Marshal.SizeOf (ev.DataType)
            else failwithf "external variable %A was expected to be contiguous" vs
        | MemConst mc -> 
            let ary = env.ConstantValues.[mc]
            ary.Storage.ByteData, 0            

    /// gets device memory and offset in bytes for an internal allocation or external reference
    let getDevMemForManikin (env: CudaExecEnvT) (manikin: ArrayNDManikinT) =
        getDevMem env manikin.Storage

    /// gets host memory for an external reference
    let getHostRegMemForManikin (env: CudaExecEnvT) (manikin: ArrayNDManikinT) =
        match manikin.Storage with
        | MemExternal vs ->
            let hv = env.HostVar.[vs]
            if ArrayND.offset hv = 0 && ArrayND.isC hv then
                ArrayNDHostReg.getCudaRegisteredMemory hv
            else
                failwithf "host variable %A was expected to be contiguous \
                           with zero offset" vs
        | _ -> failwithf "host variable must be of type ExternalMem" 

    /// gets an IArrayNDCudaT in device memory for the specified manikin
    let getArrayNDForManikin (env: CudaExecEnvT) (manikin: ArrayNDManikinT) =
        let devMem, offset = getDevMemForManikin env manikin
        let typ = manikin |> ArrayNDManikin.typeName |> TypeName.getType
        ArrayNDCuda.fromPtrAndType (devMem.DevicePointer + SizeT offset) typ (manikin.Layout)

    /// gets a CudaTexObject
    let getTextureObj (env: CudaExecEnvT) (tex: TextureObjectT) =
        env.TextureObject.[tex].TexObject


[<AutoOpen>]
module ArgTemplates =

    /// CUDA C++ argument template
    type ICudaArgTmpl =
        abstract member CPPTypeName : string
        abstract member GetArg : CudaExecEnvT -> CUstream -> obj 

    /// CUDA C++ argument template for values that are passed by value in an array
    type ICudaArrayMemberArgTmpl<'T when 'T :> ValueType> =
        abstract member CPPTypeName : string
        abstract member GetArg : CudaExecEnvT -> 'T

    /// CUDA C++ operation functor description
    type ICudaOp =
        abstract member IsIndexed : bool  

    type ICudaOpAndArgTmpl =
        inherit ICudaOp
        inherit ICudaArgTmpl

    /// CUDA device memory range template
    type IDevMemRngTmpl =
        abstract member GetRng : CudaExecEnvT -> DevMemRngT

    /// CUDA host memory range template
    type IHostMemRngTmpl =
        abstract member GetRng : CudaExecEnvT -> HostMemRngT

    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    /// C++ ArrayND with static shape and static offset/stride
    type ArrayNDSSArg =
        val Dummy : IntPtr 
        val Data : IntPtr
        new (data: IntPtr) = {Dummy = nativeint 0xdeaddead; Data = data}

    /// Literal C++ typename 
    type CPPTemplateValue (cppTypeName) =
        interface ICudaArgTmpl with
            member this.CPPTypeName = cppTypeName
            member this.GetArg env strm = failwith "TemplateValue has no argument value"

    /// ArrayND argument template
    type ArrayNDArgTmpl (manikin: ArrayNDManikinT) = 
        // TShape is ShapeStaicXD and TStride is StrideStaticXD.
        interface ICudaArgTmpl with
            member this.CPPTypeName = manikin.CPPType
            member this.GetArg env strm =
                // C++ struct just contains the pointer to data memory
                let mem, offset = CudaExecEnv.getDevMemForManikin env manikin
                let ptr = mem.DevicePointer + SizeT offset |> CudaSup.getIntPtr
                ArrayNDSSArg ptr |> box

    type ArrayNDSDArgTmpl (manikin: ArrayNDManikinT) =
        // TShape is ShapeStaicXD and TStride is StrideDynamicXD.
        interface ICudaArgTmpl with
            member this.CPPTypeName = manikin.DynamicCPPType
            member this.GetArg env strm =
                // currently this cannot be passed as an argument
                failwith "passing ArrayNDSDArg is not implemented"

    type SizeTPtrFromArrayNDIdxTmpl (manikinOpt: ArrayNDManikinT option) = 
        do 
            match manikinOpt with
            | Some manikin ->
                if manikin.DataType <> typeof<int> then 
                    failwith "SizeTPtrFromArrayNDIdxTmpl manikin must be of type int"
                if ArrayND.nDims manikin <> 0 then 
                    failwith "SizeTPtrFromArrayNDIdxTmpl manikin must be a scalar"
                if ArrayND.offset manikin <> 0 then 
                    failwith "SizeTPtrFromArrayNDIdxTmpl manikin must have zero offset"
            | None -> ()

        interface ICudaArrayMemberArgTmpl<IntPtr> with
            member this.CPPTypeName = "size_t *"
            member this.GetArg env =
                match manikinOpt with
                | Some manikin ->
                    // pass pointer to (only) element
                    let mem, offset = CudaExecEnv.getDevMemForManikin env manikin
                    mem.DevicePointer + SizeT offset |> CudaSup.getIntPtr
                | None -> IntPtr.Zero

    type CPPArrayTmpl<'T when 'T :> ValueType> (valueTmpls: ICudaArrayMemberArgTmpl<'T> list) =       
        interface ICudaArgTmpl with
            member this.CPPTypeName = 
                sprintf "Array<%s, %d>" (valueTmpls.Head.CPPTypeName) (List.length valueTmpls)
            member this.GetArg env strm =
                let argVals =
                    valueTmpls
                    |> List.map (fun vt -> vt.GetArg env)
                    |> List.toArray
                PassArrayByVal.passArrayByValue argVals

    type NullPtrArgTmpl () =
        interface ICudaArgTmpl with
            member this.CPPTypeName = "void *"
            member this.GetArg env strm = box (System.IntPtr 0) 

    type BytePtrArgTmpl (memManikin) =
        interface ICudaArgTmpl with
            member this.CPPTypeName = "char *"
            member this.GetArg env strm = 
                let storage = 
                    match memManikin with
                    | MemAlloc im -> env.InternalMem.[im]
                    | MemExternal vs -> env.ExternalVar.[vs].Storage.ByteData
                    | MemConst mc -> env.ConstantValues.[mc].Storage.ByteData
                storage.DevicePointer |> CudaSup.getIntPtr |> box

    type SizeTArgTmpl (value: int) =
        interface ICudaArgTmpl with
            member this.CPPTypeName = "size_t"
            member this.GetArg env strm = box (nativeint value) 

//    type CUdeviceptrArrayArgTmpl (MemManikinT) =
//        interface ICudaArgTmpl with
//            member this.CPPTypeName = "CUdeviceptr **"
//            member this.GetArg env strm = box (nativeint value) 

    type ExecStreamArgTmpl () =
        interface ICudaArgTmpl with
            member this.CPPTypeName = "CUstream"
            member this.GetArg env strm = box strm

    /// device memory range over the elements of a contiguous ArrayND
    type ArrayNDDevMemRngTmpl (manikin: ArrayNDManikinT) =
        do if not (ArrayND.isC manikin) then failwith "manikin for MemRng is not contiguous"
        interface IDevMemRngTmpl with
            member this.GetRng env =
                let mem, offset = CudaExecEnv.getDevMemForManikin env manikin
                {DeviceMem = mem
                 OffsetInBytes = offset + ArrayNDManikin.offsetInBytes manikin
                 LengthInBytes = ArrayNDManikin.sizeInBytes manikin}
    
    /// registered host memory range over the elements of a contiguous ArrayND    
    type ArrayNDHostRegMemRngTmpl (manikin: ArrayNDManikinT) =
        do if not (ArrayND.isC manikin) then failwith "manikin for MemRng is not contiguous"
        interface IHostMemRngTmpl with
            member this.GetRng env =
                {HostMem = CudaExecEnv.getHostRegMemForManikin env manikin;
                 OffsetInBytes = ArrayNDManikin.offsetInBytes manikin;
                 LengthInBytes = ArrayNDManikin.sizeInBytes manikin;}      

    /// checks that the specified manikin is usable with BLAS
    let checkBlasManikin isBatch manikin =
        let nDims = ArrayND.nDims manikin
        match isBatch with
        | true when nDims < 2 -> failwith "Batched ArrayND for BLAS requires 2 or more dimensions"
        | false when nDims <> 2 -> failwith "ArrayND for use with BLAS must be 2-dimensional" 
        | _ -> ()

        if not ((manikin |> ArrayNDManikin.typeName |> TypeName.getType).Equals(typeof<single>)) then
            failwith "CUBLAS currently requires single values"

        let stride, shape = ArrayND.stride manikin, ArrayND.shape manikin
        match stride.[nDims-2 ..], shape.[nDims-2 ..] with
        | [0; _], _ -> 
            failwithf "ArrayND for use with BLAS cannot be broadcasted in first dimension"
        | [m; _], [_; ms] when m < ms -> 
            failwithf "ArrayND for use with BLAS must have leading stride >= last dimension \
                       but has shape %A and stride %A" shape stride
        | [_; n], _ when n <> 1 -> 
            failwithf "ArrayND for use with BLAS must be continguous in last dimension \
                       but has shape %A and stride stride %A" shape stride
        | _ , _-> ()

    /// BLAS view of ArrayND. The ArrayND is implicitly transposed and exposed as a "float *"
    type BlasTransposedMatrixTmpl (manikin: ArrayNDManikinT) =
        // All CUBLAS calls use Fortran matrices. This means:
        // - one-based indexing
        // - column major
        // For ArrayND this translates to:
        // CUBLAS #columns    = Shape.[0]
        // CUBLAS #rows       = Shape.[1]
        // CUBLAS leading dim = Stride.[0] >= 1 (no broadcasting)
        // Stride.[1] must be 1.

        do checkBlasManikin false manikin       

        member this.Manikin = manikin

        member this.GetLeadingDimension env =
            (ArrayND.stride manikin).[0] 

        member this.GetColumns env =
            (ArrayND.shape manikin).[0]

        member this.GetRows env =
            (ArrayND.shape manikin).[1]

        member this.GetColumnsForOp env op =
            match op with 
            | CudaBlas.Operation.NonTranspose -> this.GetColumns env
            | CudaBlas.Operation.Transpose 
            | CudaBlas.Operation.ConjugateTranspose -> this.GetRows env
            | _ -> failwithf "unknown CudaBlas.Operation %A" op

        member this.GetRowsForOp env op =
            match op with 
            | CudaBlas.Operation.NonTranspose -> this.GetRows env
            | CudaBlas.Operation.Transpose 
            | CudaBlas.Operation.ConjugateTranspose -> this.GetColumns env
            | _ -> failwithf "unknown CudaBlas.Operation %A" op

        member this.GetVar env =
            let devVar, memOffset = CudaExecEnv.getDevMemForManikin env manikin
            let offset = memOffset + ArrayNDManikin.offsetInBytes manikin
            new CudaDeviceVariable<single>(devVar.DevicePointer + BasicTypes.SizeT(offset), 
                                            devVar.SizeInBytes - offset)

        interface ICudaArgTmpl with
            member this.CPPTypeName = "float"
            member this.GetArg env strm = this.GetVar env |> box

    /// BLAS view of ArrayND. The ArrayND is implicitly transposed and exposed as a "(float *)[]".
    /// All but the last two dimensions are exposed as batches.
    type BlasTransposedMatrixBatchTmpl (manikin:         ArrayNDManikinT, 
                                        ptrAryDevMem:    MemManikinT,
                                        ptrAryHostMem:   MemManikinT) =

        let nDims = ArrayND.nDims manikin
        let rowDim = nDims - 2
        let colDim = nDims - 1
        let batchShp = manikin.Shape.[0 .. nDims-3]
        let nSmpls = batchShp |> List.fold (*) 1      

        do checkBlasManikin true manikin   
            
        new (manikin: ArrayNDManikinT, memAllocator: MemAllocatorT) =
            let nSmpls = manikin.Shape.[0 .. manikin.NDims-3] |> List.fold (*) 1      
            let ptrAryDevMem = memAllocator TypeName.ofType<CUdeviceptr> nSmpls MemAllocDev
            let ptrAryHostMem = memAllocator TypeName.ofType<CUdeviceptr> nSmpls MemAllocRegHost
            BlasTransposedMatrixBatchTmpl(manikin, ptrAryDevMem, ptrAryHostMem)        

        member this.NSamples = nSmpls
        member this.Manikin = manikin
        member this.LeadingDimension = (ArrayND.stride manikin).[rowDim] 
        member this.Columns = (ArrayND.shape manikin).[rowDim]
        member this.Rows = (ArrayND.shape manikin).[colDim]

        member this.GetColumnsForOp op =
            match op with 
            | CudaBlas.Operation.NonTranspose -> this.Columns 
            | CudaBlas.Operation.Transpose 
            | CudaBlas.Operation.ConjugateTranspose -> this.Rows 
            | _ -> failwithf "unknown CudaBlas.Operation %A" op

        member this.GetRowsForOp op =
            match op with 
            | CudaBlas.Operation.NonTranspose -> this.Rows 
            | CudaBlas.Operation.Transpose 
            | CudaBlas.Operation.ConjugateTranspose -> this.Columns 
            | _ -> failwithf "unknown CudaBlas.Operation %A" op

        member this.GetPointerArrayValues env = 
            let devVar, memOffset = CudaExecEnv.getDevMemForManikin env manikin                
            [| for idx in ArrayNDLayout.allIdxOfShape batchShp do
                let offset = memOffset + ArrayNDManikin.addrInBytes (idx @ [0; 0]) manikin
                yield devVar.DevicePointer + BasicTypes.SizeT(offset) |]
            
        member this.GetPointerArrayDevice env = 
            let devVar, _ = CudaExecEnv.getDevMem env ptrAryDevMem
            new CudaDeviceVariable<CUdeviceptr> (devVar.DevicePointer, devVar.SizeInBytes) 

        member this.GetPointerArrayHost env = 
            CudaExecEnv.getHostRegMem env ptrAryHostMem

        interface ICudaArgTmpl with
            member this.CPPTypeName = "float *" 
            member this.GetArg env strm = this.GetPointerArrayDevice env |> box


    /// BLAS int array. For example it is used for pivot and info arrays of CUDA LAPACK routines.
    type BlasIntArrayTmpl (mem: MemManikinT) =

        new (size: int, memAllocator: MemAllocatorT) = 
            let mem = memAllocator TypeName.ofType<int> size MemAllocDev
            BlasIntArrayTmpl mem

        member this.GetVar env =
            let devVar, _ = CudaExecEnv.getDevMem env mem
            new CudaDeviceVariable<int> (devVar.DevicePointer, devVar.SizeInBytes)


    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    /// const value elementwise operation C++ structure
    type ConstEOpArg<'T when 'T: struct> =
        val Value: 'T
        new (value: 'T) = {Value = value}

    type ConstEOpArgTmpl<'T> (value: 'T) =
        interface ICudaArgTmpl with
            member this.CPPTypeName = "ConstEOp_t"
            member this.GetArg env strm = 
                match box value with
                | :? single as n -> ConstEOpArg(n) :> obj
                | :? double as n -> ConstEOpArg(n) :> obj
                | :? int as n -> ConstEOpArg(n) :> obj
                | :? byte as n -> ConstEOpArg(n) :> obj
                | _ -> failwithf "unsupported type %A" (value.GetType())
        interface ICudaOp with
            member this.IsIndexed = false
        interface ICudaOpAndArgTmpl


    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    /// check finite elementwise operation C++ structure
    type CheckFiniteIEOpArg<'T when 'T: struct> =
        val NonFiniteCountPtr: CUdeviceptr
        [<MarshalAs(UnmanagedType.ByValTStr, SizeConst=50)>] val Name: string
        new (nonFiniteCountPtr, name) = 
            {NonFiniteCountPtr=nonFiniteCountPtr; Name=name}

    type CheckFiniteIEOpArgTmpl<'T> (nonFiniteCount: ArrayNDManikinT,
                                     name:           string) =

        interface ICudaArgTmpl with
            member this.CPPTypeName = "CheckFiniteIEOp_t"
            member this.GetArg env strm = 
                let devVar, _ = CudaExecEnv.getDevMemForManikin env nonFiniteCount
                CheckFiniteIEOpArg (devVar.DevicePointer, name) |> box
        interface ICudaOp with
            member this.IsIndexed = true
        interface ICudaOpAndArgTmpl


    /// 1d interpolation op C++ structure
    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    type Interpolate1DEOpArg =
        val Tbl: CUtexObject
        val MinArg0: single
        val Resolution0: single
        val Offset: single

        new (tbl, minArg0, resolution0, offset) = 
            {Tbl=tbl; MinArg0=minArg0; Resolution0=resolution0; Offset=offset}

    /// 2d interpolation op C++ structure
    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    type Interpolate2DEOpArg =
        val Tbl: CUtexObject
        val MinArg0: single
        val Resolution0: single
        val MinArg1: single
        val Resolution1: single
        val Offset: single

        new (tbl, minArg0, resolution0, minArg1, resolution1, offset) = 
            {Tbl=tbl; 
             MinArg0=minArg0; Resolution0=resolution0; 
             MinArg1=minArg1; Resolution1=resolution1; 
             Offset=offset}

    /// 3d interpolation op C++ structure
    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    type Interpolate3DEOpArg =
        val Tbl: CUtexObject
        val MinArg0: single
        val Resolution0: single
        val MinArg1: single
        val Resolution1: single
        val MinArg2: single
        val Resolution2: single
        val Offset: single

        new (tbl, minArg0, resolution0, minArg1, resolution1, minArg2, resolution2, offset) = 
            {Tbl=tbl; 
             MinArg0=minArg0; Resolution0=resolution0; 
             MinArg1=minArg1; Resolution1=resolution1; 
             MinArg2=minArg2; Resolution2=resolution2;
             Offset=offset}


    type InterpolateEOpArgTmpl (ip:           IInterpolator,
                                compileEnv:   CudaCompileEnvT) =

        let tbl = 
            match Expr.getInterpolatorTableAsIArrayNDT ip with
            | :? IArrayNDCudaT as tbl -> tbl
            | _ -> failwith "interpolation table must be stored on CUDA device"
    
        do 
            if ip.NDims > 3 then
                failwith "interpolation on CUDA device is currently only supported for up to 3 dimensions"
            if tbl.DataType <> typeof<single> then
                failwith "interpolation on CUDA device is currently only supported for the single data type"

        let texture, needInit, tblCnst =
            if compileEnv.InterpolatorTextures.ContainsKey ip then
                compileEnv.InterpolatorTextures.[ip], false, None
            else
                let tblCnst = CudaCompileEnv.newConstant compileEnv tbl
                let rec adrModeForDim dim =
                    if dim >= ip.NDims then adrModeForDim (dim - 1)
                    else 
                        match ip.Outside.[dim] with
                        | Zero -> CUAddressMode.Border
                        | Nearest -> CUAddressMode.Clamp
                let filterMode =
                    match ip.Mode with
                    | InterpolateLinearaly -> CUFilterMode.Linear
                    | InterpolateToLeft -> CUFilterMode.Point
                let desc =
                    CudaTextureDescriptor (adrModeForDim 0, adrModeForDim 1, adrModeForDim 2, 
                                           filterMode, CUTexRefSetFlags.None)
                let t = CudaCompileEnv.newTextureObject compileEnv tblCnst desc
                compileEnv.InterpolatorTextures.Add (ip, t)
                t, true, Some tblCnst

        member this.NeedInit = needInit

        interface ICudaArgTmpl with
            member this.CPPTypeName = 
                match ip.NDims with
                | 1 -> "Interpolate1DEOp_t"
                | 2 -> "Interpolate2DEOp_t"
                | 3 -> "Interpolate3DEOp_t"
                | _ -> failwith "unsupported"
            member this.GetArg env strm = 
                let texObj = CudaExecEnv.getTextureObj env texture
                let offset = 
                    match ip.Mode with
                    | InterpolateLinearaly -> 0.5f
                    | InterpolateToLeft -> 0.0f

                match ip.NDims with
                | 1 -> Interpolate1DEOpArg (texObj.TexObject, 
                                            ip.MinArg.[0], ip.Resolution.[0],
                                            offset)
                       |> box
                | 2 -> Interpolate2DEOpArg (texObj.TexObject, 
                                            ip.MinArg.[0], ip.Resolution.[0],
                                            ip.MinArg.[1], ip.Resolution.[1],
                                            offset)
                       |> box
                | 3 -> Interpolate3DEOpArg (texObj.TexObject, 
                                            ip.MinArg.[0], ip.Resolution.[0],
                                            ip.MinArg.[1], ip.Resolution.[1],
                                            ip.MinArg.[2], ip.Resolution.[2],
                                            offset)
                       |> box
                | _ -> failwith "unsupported"

        interface ICudaOp with
            member this.IsIndexed = false
        interface ICudaOpAndArgTmpl



    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    /// elmentwise operation C++ structure with no fields
    type NoArgEOpArg = struct end
    
    /// arg template for elmentwise op C++ structure 
    type NoArgEOpArgTmpl (cppTypeName: string, indexed: bool) =
        interface ICudaArgTmpl with
            member this.CPPTypeName = cppTypeName
            member this.GetArg env strm = NoArgEOpArg() |> box
        interface ICudaOp with
            member this.IsIndexed = indexed

    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    /// elments operation C++ structure with no fields
    type ElementsOpArg = struct end
    
    /// arg template for elements op C++ structure 
    type ElementsOpArgTmpl (cppTypeName: string) =
        interface ICudaArgTmpl with
            member this.CPPTypeName = cppTypeName
            member this.GetArg env strm = ElementsOpArg() |> box
        interface ICudaOp with
            member this.IsIndexed = true


[<AutoOpen>]
module NativeFunctionDelegates =

    [<CPPFuncName("sum")>]
    type CPPSum = delegate of ArrayNDSSArg * ArrayNDSSArg * CUstream * IntPtr * nativeint -> unit

    [<CPPFuncName("sumLastAxis")>]
    type CPPSumLastAxis = delegate of ArrayNDSSArg * ArrayNDSSArg * CUstream * IntPtr * nativeint -> unit
