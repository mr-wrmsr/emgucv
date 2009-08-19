using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using Emgu.CV.Structure;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Emgu.Util;

namespace Emgu.CV
{
   /// <summary>
   /// This class use SURF and CamShift to track object
   /// </summary>
   public class SURFTracker : DisposableObject
   {
      private SURFMatcher _matcher;
      private static int _randsacRequiredMatch = 10;

      /// <summary>
      /// Create a SURF tracker, where SURF is matched with k-d Tree
      /// </summary>
      /// <param name="modelFeatures">The SURF feature from the model image</param>
      public SURFTracker(SURFFeature[] modelFeatures)
      {
         _matcher = new SURFMatcher(modelFeatures);
      }

      /// <summary>
      /// Create a SURF tracker, where SURF is matched with k-d Tree
      /// </summary>
      /// <param name="naive">A good value is 50</param>
      /// <param name="rho">A good value is .7</param>
      /// <param name="tau">A good value is .1</param>
      /// <param name="modelFeatures">The SURF feature from the model image</param>
      public SURFTracker(SURFFeature[] modelFeatures, int naive, double rho, double tau)
      {
         _matcher = new SURFMatcher(modelFeatures, naive, rho, tau);
      }

      /// <summary>
      /// Use camshift to track the feature
      /// </summary>
      /// <param name="observedFeatures">The feature found from the observed image</param>
      /// <param name="initRegion">The predicted location of the model in the observed image. If not known, use MCvBox2D.Empty as default</param>
      /// <param name="priorMask">The mask that should be the same size as the observed image. Contains a priori value of the probability a match can be found. If you are not sure, pass an image fills with 1.0s</param>
      /// <returns>If a match is found, the homography projection matrix is returned. Otherwise null is returned</returns>
      public HomographyMatrix CamShiftTrack(SURFFeature[] observedFeatures, MCvBox2D initRegion, Image<Gray, Single> priorMask)
      {
         double matchDistanceRatio = 0.8;

         using (Image<Gray, Single> matchMask = new Image<Gray, Single>(priorMask.Size))
         {
            #region get the list of matched point on the observed image
            Single[, ,] matchMaskData = matchMask.Data;

            MatchedSURFFeature[] matchedFeature = _matcher.MatchFeature(observedFeatures, 2, 20);
            SortIndividualMatchedFeatureByDistance(matchedFeature);
            matchedFeature = VoteForUniqueness(matchedFeature, matchDistanceRatio);

            foreach (MatchedSURFFeature f in matchedFeature)
            {
               PointF p = f.ObservedFeature.Point.pt;
               matchMaskData[(int)p.Y, (int)p.X, 0] = 1.0f / (float) f.SimilarFeatures[0].Distance;
            }
            #endregion

            Rectangle startRegion;
            if (initRegion.Equals(MCvBox2D.Empty))
               startRegion = matchMask.ROI;
            else
            {
               startRegion = PointCollection.BoundingRectangle(initRegion.GetVertices());
               if (startRegion.IntersectsWith(matchMask.ROI))
                  startRegion.Intersect(matchMask.ROI);
            }

            CvInvoke.cvMul(matchMask.Ptr, priorMask.Ptr, matchMask.Ptr, 1.0);

            MCvConnectedComp comp;
            MCvBox2D currentRegion;
            //Updates the current location
            CvInvoke.cvCamShift(matchMask.Ptr, startRegion, new MCvTermCriteria(10, 1.0e-8), out comp, out currentRegion);

            #region find the SURF features that belongs to the current Region
            MatchedSURFFeature[] featuesInCurrentRegion;
            using (MemStorage stor = new MemStorage())
            {
               Contour<System.Drawing.PointF> contour = new Contour<PointF>(stor);
               contour.PushMulti(currentRegion.GetVertices(), Emgu.CV.CvEnum.BACK_OR_FRONT.BACK);

               CvInvoke.cvBoundingRect(contour.Ptr, 1); //this is required before calling the InContour function

               featuesInCurrentRegion = Array.FindAll(matchedFeature,
                  delegate(MatchedSURFFeature f)
                  { return contour.InContour(f.ObservedFeature.Point.pt) >= 0; });
            }
            #endregion

            return GetHomographyMatrixFromMatchedFeatures(featuesInCurrentRegion);
         }
      }

      /// <summary>
      /// Detect the if the model features exist in the observed features. If true, an homography matrix is returned, otherwise, null is returned.
      /// </summary>
      /// <param name="observedFeatures">The observed features</param>
      /// <param name="uniquenessThreshold">The distance different ratio which a match is consider unique, a good number will be 0.8</param>
      /// <returns>If the model features exist in the observed features, an homography matrix is returned, otherwise, null is returned.</returns>
      public HomographyMatrix Detect(SURFFeature[] observedFeatures, double uniquenessThreshold)
      {
         MatchedSURFFeature[] matchedGoodFeatures = MatchFeature(observedFeatures, 2, 20);

         //Stopwatch w1 = Stopwatch.StartNew();
         matchedGoodFeatures = VoteForUniqueness(matchedGoodFeatures, uniquenessThreshold);
         //Trace.WriteLine(w1.ElapsedMilliseconds);

         if (matchedGoodFeatures.Length < 4)
            return null;

         //Stopwatch w2 = Stopwatch.StartNew();
         matchedGoodFeatures = VoteForSizeAndOrientation(matchedGoodFeatures, 1.5, 20);
         //Trace.WriteLine(w2.ElapsedMilliseconds);

         if (matchedGoodFeatures.Length < 4)
            return null;

         return GetHomographyMatrixFromMatchedFeatures(matchedGoodFeatures);
      }

      /// <summary>
      /// Recover the homography matrix using RANDSAC. If the matrix cannot be recovered, null is returned.
      /// </summary>
      /// <param name="matchedFeatures">The Matched Features, only the first ModelFeature will be considered</param>
      /// <returns>The homography matrix, if it cannot be found, null is returned</returns>
      public static HomographyMatrix GetHomographyMatrixFromMatchedFeatures(MatchedSURFFeature[] matchedFeatures)
      {
         if (matchedFeatures.Length < 4)
            return null;

         HomographyMatrix homography;
         if (matchedFeatures.Length < _randsacRequiredMatch)
         {  // Too few points for randsac, use 4 points only
            PointF[] pts1 = new PointF[4];
            PointF[] pts2 = new PointF[4];
            for (int i = 0; i < 4; i++)
            {
               pts1[i] = matchedFeatures[i].SimilarFeatures[0].Feature.Point.pt;
               pts2[i] = matchedFeatures[i].ObservedFeature.Point.pt;
            }
            homography = CameraCalibration.GetPerspectiveTransform(pts1, pts2);
         }
         else
         {
            //use randsac to find the Homography Matrix
            PointF[] pts1 = new PointF[matchedFeatures.Length];
            PointF[] pts2 = new PointF[matchedFeatures.Length];
            for (int i = 0; i < matchedFeatures.Length; i++)
            {
               pts1[i] = matchedFeatures[i].SimilarFeatures[0].Feature.Point.pt;
               pts2[i] = matchedFeatures[i].ObservedFeature.Point.pt;
            }

            homography = CameraCalibration.FindHomography(
               pts1, //points on the model image
               pts2, //points on the observed image
               CvEnum.HOMOGRAPHY_METHOD.RANSAC,
               3);
            if (homography == null)
               return null;
         }

         if (homography.IsValid(10))
            return homography;
         else
         {
            homography.Dispose();
            return null;
         }
      }

      /// <summary>
      /// For each MatchedSURFFeature, sort the model SURF feature by distance (closer distance has smaller index).
      /// </summary>
      /// <param name="matchedFeatures">The matched features to be sorted</param>
      private static void SortIndividualMatchedFeatureByDistance(MatchedSURFFeature[] matchedFeatures)
      {
         //SortedList<double, SURFFeature> sortedList = new SortedList<double, SURFFeature>();
         List<SimilarFeature> pairs = new List<SimilarFeature>();
         foreach (MatchedSURFFeature ms in matchedFeatures)
         {
            switch (ms.SimilarFeatures.Length)
            {
               case 1: //no need to sort if only 1 match
                  break;
               case 2: //fast implementation for 2 features
                  if (ms.SimilarFeatures[1].Distance < ms.SimilarFeatures[0].Distance)
                  {
                     SimilarFeature tmp = ms.SimilarFeatures[0];
                     ms.SimilarFeatures[0] = ms.SimilarFeatures[1];
                     ms.SimilarFeatures[1] = tmp;
                  }
                  break;
               default: //generic sort for 2 or more matches
                  Array.Sort<SimilarFeature>( ms.SimilarFeatures, CompareSimilarFeature);
                  break;
            }
         }
      }

      public struct SimilarFeature
      {
         private double _distance;
         public double Distance
         {
            get
            {
               return _distance;
            }
            set
            {
               _distance = value;
            }
         }

         private SURFFeature _feature;

         public SURFFeature Feature
         {
            get
            {
               return _feature;
            }
            set
            {
               _feature = value;
            }
         }

         public SimilarFeature(double distance, SURFFeature feature)
         {
            _distance = distance;
            _feature = feature;
         }
      }

      private static int CompareSimilarFeature(SimilarFeature f1, SimilarFeature f2)
      {
         if (f1.Distance < f2.Distance)
            return -1;
         if (f1.Distance == f2.Distance)
            return 0;
         else
            return 1;
      }

      /// <summary>
      /// Sorted the matched SURF feature, such that a matchedFeature with smaller distance has a lower index
      /// </summary>
      /// <param name="matchedFeatures">The matched features to be sorted</param>
      public static void SortMatchedSURFFeatures(MatchedSURFFeature[] matchedFeatures)
      {
         SortedList<double, MatchedSURFFeature> list = new SortedList<double, MatchedSURFFeature>(matchedFeatures.Length);
         foreach (MatchedSURFFeature ms in matchedFeatures)
            list.Add(ms.SimilarFeatures[0].Distance, ms);

         list.Values.CopyTo(matchedFeatures, 0);
      }

      /// <summary>
      /// Filter the matched Features, such that if a match is not unique, it is rejected.
      /// </summary>
      /// <param name="matchedFeatures">The Matched SURF features, each of them has the model feature sorted by distance. (e.g. SortMatchedFeaturesByDistance )</param>
      /// <param name="uniquenessThreshold">The distance different ratio which a match is consider unique, a good number will be 0.8</param>
      /// <returns>The filtered matched SURF Features</returns>
      public static MatchedSURFFeature[] VoteForUniqueness(MatchedSURFFeature[] matchedFeatures, double uniquenessThreshold)
      {
         return Array.FindAll<MatchedSURFFeature>(matchedFeatures,
            delegate(MatchedSURFFeature f)
            {
               return
                  f.SimilarFeatures.Length == 1 //this is the only match
                  || f.SimilarFeatures[0].Distance / f.SimilarFeatures[1].Distance <= uniquenessThreshold; //if the first model feature is a good match 
            });
      }

      /// <summary>
      /// Eliminate the matched features whose scale and rotation do not aggree with the majority's scale and rotation.
      /// </summary>
      /// <param name="rotationBins">The numbers of bins for rotation, a good value might be 20 (which means each bin covers 18 degree)</param>
      /// <param name="scaleIncrement">This determins the different in scale for neighbour hood bins, a good value might be 1.5 (which means matched features in bin i+1 is scaled 1.5 times larger than matched features in bin i</param>
      /// <param name="matchedFeatures">The matched feature that will be participated in the voting. For each matchedFeatures, only the zero indexed ModelFeature will be considered.</param>
      public static MatchedSURFFeature[] VoteForSizeAndOrientation(MatchedSURFFeature[] matchedFeatures, double scaleIncrement, int rotationBins)
      {
         int elementsCount = matchedFeatures.Length;
         float[] scales = new float[elementsCount];
         float[] rotations = new float[elementsCount];
         float[] flags = new float[elementsCount];
         float minScale = float.MaxValue;
         float maxScale = float.MinValue;

         for (int i = 0; i < matchedFeatures.Length; i++)
         {
            float scale = (float)matchedFeatures[i].ObservedFeature.Point.size / (float)matchedFeatures[i].SimilarFeatures[0].Feature.Point.size;
            scale = (float)Math.Log10(scale);
            scales[i] = scale;
            if (scale < minScale) minScale = scale;
            if (scale > maxScale) maxScale = scale;

            float rotation = matchedFeatures[i].ObservedFeature.Point.dir - matchedFeatures[i].SimilarFeatures[0].Feature.Point.dir;
            rotations[i] = rotation < 0.0 ? rotation + 360 : rotation;
         }

         int scaleBinSize = (int)Math.Max(((maxScale - minScale) / Math.Log10(scaleIncrement)), 1);

         using (DenseHistogram h = new DenseHistogram(new int[] { scaleBinSize, rotationBins }, new RangeF[] { new RangeF(minScale, maxScale), new RangeF(0, 360) }))
         {
            GCHandle scaleHandle = GCHandle.Alloc(scales, GCHandleType.Pinned);
            GCHandle rotationHandle = GCHandle.Alloc(rotations, GCHandleType.Pinned);
            GCHandle flagsHandle = GCHandle.Alloc(flags, GCHandleType.Pinned);

            using (Matrix<float> flagsMat = new Matrix<float>(1, elementsCount, flagsHandle.AddrOfPinnedObject()))
            using (Matrix<float> scalesMat = new Matrix<float>(1, elementsCount, scaleHandle.AddrOfPinnedObject()))
            using (Matrix<float> rotationsMat = new Matrix<float>(1, elementsCount, rotationHandle.AddrOfPinnedObject()))
            {
               h.Calculate(new Matrix<float>[] { scalesMat, rotationsMat }, true, null);

               float minVal, maxVal;
               int[] minLoc, maxLoc;
               h.MinMax(out minVal, out maxVal, out minLoc, out maxLoc);

               h.Threshold(maxVal * 0.5);

               CvInvoke.cvCalcBackProject(new IntPtr[] { scalesMat.Ptr, rotationsMat.Ptr }, flagsMat.Ptr, h.Ptr);
            }
            scaleHandle.Free();
            rotationHandle.Free();
            flagsHandle.Free();

            List<MatchedSURFFeature> matchedGoodFeatures = new List<MatchedSURFFeature>();
            for (int i = 0; i < matchedFeatures.Length; i++)
            {
               if (flags[i] != 0)
               {
                  matchedGoodFeatures.Add(matchedFeatures[i]);
               }
            }
            return matchedGoodFeatures.ToArray();
         }
      }

      /// <summary>
      /// Release unmanaged memory
      /// </summary>
      protected override void DisposeObject()
      {
      }

      /// <summary>
      /// Release the memory assocaited with this SURF Tracker
      /// </summary>
      protected override void ReleaseManagedResources()
      {
         _matcher.Dispose();
      }

      /// <summary>
      /// Match the SURF feature from the observed image to the features from the model image
      /// </summary>
      /// <param name="observedFeatures">The SURF feature from the observed image</param>
      /// <param name="k">The number of neighbors to find</param>
      /// <param name="emax">For k-d tree only: the maximum number of leaves to visit.</param>
      /// <returns>The matched features</returns>
      public MatchedSURFFeature[] MatchFeature(SURFFeature[] observedFeatures, int k, int emax)
      {
         MatchedSURFFeature[] res = _matcher.MatchFeature(observedFeatures, k, emax);
         SortIndividualMatchedFeatureByDistance(res);
         return res;
      }

      /// <summary>
      /// The matched SURF feature
      /// </summary>
      public struct MatchedSURFFeature
      {
         /// <summary>
         /// The observed feature
         /// </summary>
         public SURFFeature ObservedFeature;

         private SimilarFeature[] _similarFeatures;

         public SimilarFeature[] SimilarFeatures
         {
            get
            {
               return _similarFeatures;
            }
            set
            {
               _similarFeatures = value;
            }
         }

         /// <summary>
         /// Create a matched feature structure.
         /// </summary>
         /// <param name="observedFeature">The feature from the observed image</param>
         /// <param name="modelFeatures">The matched feature from the model</param>
         /// <param name="dist">The distances between the feature from the observerd image and the matched feature from the model image</param>
         public MatchedSURFFeature(SURFFeature observedFeature, SURFFeature[] modelFeatures, double[] dist)
         {
            ObservedFeature = observedFeature;
            _similarFeatures = new SimilarFeature[modelFeatures.Length];
            for (int i = 0; i < modelFeatures.Length; i++)
               _similarFeatures[i] = new SimilarFeature(dist[i], modelFeatures[i]); 

         }
      }

      /// <summary>
      /// A simple class that use two feature trees (postive/negative laplacian) to match SURF features. 
      /// </summary>
      private class SURFMatcher : DisposableObject
      {
         private SURFFeature[] _modelFeatures;

         private FeatureTree _modelFeatureTree;

         /// <summary>
         /// Create k-d feature trees using the SURF feature extracted from the model image.
         /// </summary>
         /// <param name="modelFeatures">The SURF feature extracted from the model image</param>
         public SURFMatcher(SURFFeature[] modelFeatures)
         {
            Debug.Assert(modelFeatures.Length > 0, "Model Features should have size > 0");

            _modelFeatureTree = new FeatureTree(
               Array.ConvertAll<SURFFeature, float[]>(
                  modelFeatures,
                  delegate(SURFFeature f) { return f.Descriptor; }));
            _modelFeatures = modelFeatures;
         }

         /// <summary>
         /// Create spill trees using SURF feature extracted from the model image.
         /// </summary>
         /// <param name="modelFeatures">The SURF feature extracted from the model image</param>
         /// <param name="naive">A good value is 50</param>
         /// <param name="rho">A good value is .7</param>
         /// <param name="tau">A good value is .1</param>
         public SURFMatcher(SURFFeature[] modelFeatures, int naive, double rho, double tau)
         {
            _modelFeatureTree = new FeatureTree(
               Array.ConvertAll<SURFFeature, float[]>(
                  modelFeatures,
                  delegate(SURFFeature f) { return f.Descriptor; }),
                  naive,
                  rho,
                  tau);
         }

         /// <summary>
         /// Match the SURF feature from the observed image to the features from the model image
         /// </summary>
         /// <param name="observedFeatures">The SURF feature from the observed image</param>
         /// <param name="k">The number of neighbors to find</param>
         /// <param name="emax">For k-d tree only: the maximum number of leaves to visit.</param>
         /// <returns>The matched features</returns>
         public MatchedSURFFeature[] MatchFeature(SURFFeature[] observedFeatures, int k, int emax)
         {
            return MatchFeatureWithModel(observedFeatures, _modelFeatures, _modelFeatureTree, k, emax);
         }

         private static MatchedSURFFeature[] MatchFeatureWithModel(SURFFeature[] observedFeatures, SURFFeature[] modelFeatures, FeatureTree modelFeatureTree, int k, int emax)
         {
            if (observedFeatures.Length == 0) return new MatchedSURFFeature[0];

            Matrix<Int32> result1;
            Matrix<double> dist1;

            float[][] descriptors = new float[observedFeatures.Length][];
            for (int i = 0; i < observedFeatures.Length; i++)
               descriptors[i] = observedFeatures[i].Descriptor;

            modelFeatureTree.FindFeatures(descriptors, out result1, out dist1, k, emax);

            int[,] indexes = result1.Data;
            double[,] distances = dist1.Data;

            MatchedSURFFeature[] res = new MatchedSURFFeature[observedFeatures.Length];
            List<SimilarFeature> matchedFeatures = new List<SimilarFeature>();
            
            for (int i = 0; i < res.Length; i++)
            {
               matchedFeatures.Clear();
               
               for (int j = 0; j < k; j++)
               {
                  int index = indexes[i, j];
                  if (index >= 0)
                  {
                     matchedFeatures.Add(new SimilarFeature(distances[i, j], modelFeatures[index]));
                  }
               }
             
               res[i].ObservedFeature = observedFeatures[i];
               res[i].SimilarFeatures = matchedFeatures.ToArray();
            }
            result1.Dispose();
            dist1.Dispose();
            return res;
         }

         /// <summary>
         /// Release the unmanaged memory associate with this matcher
         /// </summary>
         protected override void DisposeObject()
         {
         }

         protected override void ReleaseManagedResources()
         {
            _modelFeatureTree.Dispose();
         }
      }
   }
}
