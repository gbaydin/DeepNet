﻿namespace Models

open ArrayNDNS
open SymTensor


module GaussianProcess =
    
    /// Kernek
    type Kernel =
        /// linear kernel
        | Linear
        /// squared exponential kernel
        | SquaredExponential of single*single
    
    /// GP hyperparameters
    type HyperPars = {
        Kernel :    Kernel
        }
    
    /// GP parameters with linear kernel
    type ParsLinear = {
        HyperPars:  HyperPars
        }
    
    /// GP parameters with squared exponential kernel
    type ParsSE = {
        Lengthscale:    ExprT
        SignalVariance: ExprT
        HyperPars:  HyperPars
        }

    let internal initLengthscale l seed (shp: int list)  : ArrayNDHostT<single> =
        ArrayNDHost.scalar l
    
    let internal initSignalVariance s seed (shp: int list) : ArrayNDHostT<single> =
        ArrayNDHost.scalar s

    type Pars = LinPars of ParsLinear | SEPars of  ParsSE



    let pars (mb: ModelBuilder<_>) (hp:HyperPars) = 
        match hp.Kernel with
        | Linear -> LinPars {HyperPars = hp}
        | SquaredExponential (l,s)-> SEPars { Lengthscale = mb.Param ("Lengthscale" , [], initLengthscale l)
                                              SignalVariance = mb.Param ("SignalVariance" , [], initSignalVariance s)
                                              HyperPars = hp}
    

    /// calculates Matrix between two vectors using linear kernel
    let linearCovariance (x:ExprT) (y:ExprT) =
        x .* y
    
    /// calculates Matrix between two vectors using linear kernel
    let squaredExpCovariance (l:ExprT, sigf:ExprT) (x:ExprT) (y:ExprT) =
        let x_smpl, y_smpl  = ElemExpr.idx2
        let xvec, yvec,len,sigmaf = ElemExpr.arg4<single>
        let kse = sigmaf[] * (exp -((xvec[x_smpl] - yvec[y_smpl])***2.0f)/ (2.0f * len[]***2.0f))
        let sizeX = Expr.nElems x
        let sizeY = Expr.nElems y
        Expr.elements [sizeX;sizeY] kse [x; y;l;sigf]

    let predict (pars:Pars) x y sigmaNs x_star =
        let covMat z z' =
            match pars with
            | LinPars _ -> linearCovariance z z'
            | SEPars pars  -> squaredExpCovariance (pars.Lengthscale,pars.SignalVariance) z z'
        let K           = (covMat x x) + Expr.diagMat sigmaNs
        let Kinv        = Expr.invert K
        let K_star      = covMat x x_star
        let K_starT     = Expr.transpose K_star
        let K_starstar  = covMat x_star x_star
        
        let mean = K_starT .* Kinv .* y
        let cov = K_starstar - K_starT .* Kinv .* K_star
        mean,cov
    /// WARNING: NOT YET IMPLEMENTED, ONLY A RIMINDER FOR LATER IMPLEMENTATION!
    /// !!! CALLING THIS FUNCTION WILL ONLY CAUSE AN ERROR !!!
    let logMarginalLiklihood (pars:Pars) x y sigmaNs x_sta =
        failwith "TODO: implement logMarginalLikelihood"
