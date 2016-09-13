﻿namespace GPTransfer

open ArrayNDNS
open SymTensor
open SymTensor.Compiler.Cuda
open System
open Datasets

module TestFunctions =
    
    ///Transfers Arrays to device (either Host or DevCuda)
    let post device (x: ArrayNDT<'T>) =
        if device = DevCuda then ArrayNDCuda.toDev (x :?> ArrayNDHostT<'T>) :> ArrayNDT<'T>
        else x 
    
    ///Sampling type for Model training parameters.
    type trainData ={
        Lengthscale:    ArrayNDT<single>
        Trn_X:          ArrayNDT<single>
        Trn_T:          ArrayNDT<single>
        Trn_Sigma:      ArrayNDT<single>
        }

    ///Sampling type for Model input and prediction.
    type predOutput = {
       In_Mean:         ArrayNDT<single>
       In_Cov:          ArrayNDT<single>
       Pred_Mean:       ArrayNDT<single>
       Pred_Cov:        ArrayNDT<single>
       }

    ///Generates a random list of singles that is sorted.
    let randomSortedListOfLength (rand:Random) (minValue,maxValue) length =
        [1..length] |> List.map (fun _ -> rand.NextDouble())
        |> List.map (fun x -> (single x))
        |> List.map (fun x -> x  * (maxValue - minValue) + minValue)
        |> List.sort

    ///Generates multiple random sorted lists of singles in a 2D list
    let randomSortedLists (rand:Random) (minValue,maxValue) length = 
        List.map (fun _ -> randomSortedListOfLength rand (minValue,maxValue) length)
    
    let fsng x = single x
    let isng x = single x

    ///Generates a random polynomial of maximal power 2
    let randPolynomial (rand:Random) list = 

        let fact1 = fsng (rand.NextDouble())
        let fact2 = fsng (rand.NextDouble())
        let pow1 = isng (rand.Next(1,2))
        let pow2 = isng (rand.Next(1,2))
        list |> List.map (fun x ->   fact1 *x** pow1 - fact2 *x** pow2)
   
   ///Turns a random matrix in the form of a covariance matrix into a Psd matrix. 
    let makePsd (c: ArrayNDT<_>) =        
        c.T .* c



    ///Tests multilayer GPs with random parameters and random inputs.
    ///Saves parameters and inputs in hdf5 files to compare with other implementations (especially gpsample.py).
    let testMultiGPLayer device =

        //initiating random number generator 
        let rand = Random(1)
        //defining size parameters
        let ngps = 3
        let ntraining = 10
        let ntest = 1

        //building the model
        let mb = ModelBuilder<single> "Test"

        let nSmpls    = mb.Size "nSmpls"
        let nGPs      = mb.Size "nGPs"
        let nTrnSmpls = mb.Size "nTrnSmpls"
        
        let w =
            WeightLayer.pars (mb.Module "WL") {NInput = nGPs; NGPs = nGPs}

        let mgp = 
            MultiGPLayer.pars (mb.Module "MGP") {NGPs=nGPs; NTrnSmpls=nTrnSmpls}
        let inp_mean  : ExprT<single> = mb.Var "inp_mean"  [nSmpls; nGPs]
        let inp_cov   : ExprT<single> = mb.Var "inp_cov"   [nSmpls; nGPs; nGPs]
        mb.SetSize nGPs      ngps
        mb.SetSize nTrnSmpls ntraining
        let mi = mb.Instantiate device

        //model outputs
//        let pred_mean = MultiGPLayer.pred mgp (WeightLayer.transform w (inp_mean, inp_cov))
        let pred_mean = MultiGPLayer.pred mgp  (inp_mean, inp_cov)
//        let pred_mean,predcov = MultiGPLayer.pred mgp (inp_mean, inp_cov)
        let pred_mean= mi.Func pred_mean |> arg2 inp_mean inp_cov

//        let pred_mean, pred_cov = MultiGPLayer.pred mgp inp_mean inp_cov
//        let pred_mean_cov_fn = mi.Func (pred_mean, pred_cov) |> arg2 inp_mean inp_cov

        //creating random training vectors
        let trn_x_list = [1..ngps] |> randomSortedLists rand (-5.0f,5.0f) ntraining 
        let trn_x_host = trn_x_list |> ArrayNDHost.ofList2D

        let trn_t_list = trn_x_list |> List.map(fun list -> randPolynomial rand list)
        let trn_t_host = trn_t_list |> ArrayNDHost.ofList2D

        printfn "Trn_x =\n%A" trn_x_host
        printfn "Trn_t =\n%A" trn_t_host

        //lengthscale vectore hardcoded
        let ls_host = [1.0f; 1.5f; 2.0f] |> ArrayNDHost.ofList 
//        //random lengthscale vector
//        let ls_host = rand.UniformArrayND (0.0f,3.0f) [ngps]

        //sigma vector hardcoded
        let trn_sigma_host = (ArrayNDHost.ones<single> [ngps;ntraining]) * sqrt 0.1f


        //save train parameters
        let trainInp = {
            Lengthscale = ls_host;
            Trn_X = trn_x_host;
            Trn_T = trn_t_host;
            Trn_Sigma = trn_sigma_host}

        let trainData = [trainInp] |> Dataset.FromSamples
        let trainFileName = sprintf "TrainData.h5"
        trainData.Save(trainFileName)

        //transfer train parametters to device (Host or GPU)
        let ls_val = ls_host |> post device
        let trn_x_val = trn_x_host  |> post device
        let trn_t_val = trn_t_host  |> post device
        let trn_sigma_val = trn_sigma_host  |> post device

        mi.ParameterStorage.[!mgp.Lengthscales] <- ls_val
        mi.ParameterStorage.[!mgp.TrnX] <- trn_x_val
        mi.ParameterStorage.[!mgp.TrnT] <- trn_t_val
        mi.ParameterStorage.[!mgp.TrnSigma] <- trn_sigma_val

        let transMean,transCov = WeightLayer.transform w (inp_mean,inp_cov)
        let transTestFn1 =  mi.Func transMean |> arg2 inp_mean inp_cov
        let transTestFn2 =  mi.Func transCov  |> arg2 inp_mean inp_cov
        let initLMean,initLCov = initialLayer.transform inp_mean
        let initTestFn1 =  mi.Func initLMean |> arg1 inp_mean
        let initTestFn2 =  mi.Func initLCov |> arg1 inp_mean
        ///run GpTransferModel with random test inputs
        let randomTest () =

            //generate random test inputs
            let inp_mean_host = rand.UniformArrayND (-5.0f ,5.0f) [1;ngps]
            let inp_mean_val = inp_mean_host |> post device

            //let inp_covhost = ArrayNDHost.zeros<single> [1;ngps;ngps]
            //let inp_covhost = 0.1f * ArrayNDHost.ones [1;ngps] |> ArrayND.diagMat

            let inp_cov_host = rand.UniformArrayND (-2.0f, 2.0f) [1;ngps;ngps]
            let inp_covhost = makePsd inp_cov_host

            let inp_cov_val = inp_covhost |> post device

            //calculate predicted mean and variance
            let pred_mean = pred_mean inp_mean_val inp_cov_val

            let transMean = transTestFn1 inp_mean_val inp_cov_val
            let transCov = transTestFn2 inp_mean_val inp_cov_val

            printfn "transMean=\n%A" transMean
            printfn ""
            printfn "transCov=\n%A" transCov
            printfn ""


            let initMean = initTestFn1 inp_mean_val
            let initCov = initTestFn2 inp_mean_val

            printfn "initLMean=\n%A" initMean
            printfn ""
            printfn "initLCov=\n%A" initCov
            printfn ""


            //save inputs and predictions in sample datatype
            let testInOut = {
                In_Mean = inp_mean_host;
                In_Cov = inp_covhost;
                Pred_Mean = pred_mean;
                Pred_Cov = inp_covhost}

            //print inputs and predictions
            printfn "Lengthscales=\n%A" mi.ParameterStorage.[!mgp.Lengthscales]
            printfn "TrnX=\n%A" mi.ParameterStorage.[!mgp.TrnX]
            printfn "TrnT=\n%A" mi.ParameterStorage.[!mgp.TrnT]
            printfn "TrnSigma=\n%A" mi.ParameterStorage.[!mgp.TrnSigma]
            printfn ""
            printfn "inp_mean=\n%A" inp_mean_val
            printfn "inp_cov=\n%A" inp_cov_val
            printfn ""
            printfn "pred_mean=\n%A" pred_mean
//            printfn "pred_cov=\n%A" pred_cov

            //return sample of inputs and predictions
            testInOut

        //run ntest tests and save samples in dataset
        Dump.start "dump.h5"
        printfn "Testing Multi GP Transfer Model on %A" device
        let testList = [1..ntest]
                       |> List.map (fun n-> 
                            Dump.prefix <- sprintf "%d" n
                            randomTest () )
        Dump.stop ()

        let testData = testList |> Dataset.FromSamples
        let testFileName = sprintf "TestData.h5"
        testData.Save(testFileName)
    
    let testDatasetParser() =
        let printData path tgt sep =
            let data = DataParser.loadSingleDataset path tgt sep
            printfn "Printing %s Dataset\n target:%A" path tgt
            for smpl in data do
                printfn "Sample: %A" smpl



        let printAbloneData8() =     
            printfn "printing ablone data"
            printData "abalone.data.txt" [8] ',' 
        let printAbloneData0() =     
            printfn "printing ablone data"
            printData "abalone.data.txt" [0] ',' 


//        let printLetterRecognition() =
//            printfn "printing letter-recognition data"
//            printData "letter-recognition.data.txt" [0] ',' 

//        let printAutoMpg() =
//            printfn "printing auto-mpg data"
//            printData "auto-mpg.data.txt" ' ' [0] 
//
        printAbloneData8()
        printAbloneData0()

//        printLetterRecognition()
//        printAutoMpg()
