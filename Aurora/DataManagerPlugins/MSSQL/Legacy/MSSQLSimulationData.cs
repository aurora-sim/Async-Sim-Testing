/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Data.MSSQL
{
    /// <summary>
    /// A MSSQL Interface for the Region Server.
    /// </summary>
    public class MSSQLSimulationData : ILegacySimulationDataStore
    {
        private const string _migrationStore = "RegionStore";

        // private static FileSystemDataStore Instance = new FileSystemDataStore();
        //private static readonly ILog _Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The database manager
        /// </summary>
        private MSSQLManager _Database;
        private string m_connectionString;

        public string Name
        {
            get
            {
                return "MSSQL";
            }
        }

        /// <summary>
        /// Initialises the region datastore
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        public void Initialise(string connectionString)
        {
            m_connectionString = connectionString;
            _Database = new MSSQLManager(connectionString);


            //Migration settings
            _Database.CheckMigration(_migrationStore);
        }

        /// <summary>
        /// Dispose the database
        /// </summary>
        public void Dispose() { }

        #region SceneObjectGroup region for loading and Store of the scene.

        /// <summary>
        /// Loads the objects present in the region.
        /// </summary>
        /// <param name="regionUUID">The region UUID.</param>
        /// <returns></returns>
        public List<ISceneEntity> LoadObjects (UUID regionUUID, IScene scene)
        {
            UUID lastGroupID = UUID.Zero;

            Dictionary<UUID, SceneObjectPart> prims = new Dictionary<UUID, SceneObjectPart>();
            Dictionary<UUID, ISceneEntity> objects = new Dictionary<UUID, ISceneEntity> ();
            SceneObjectGroup grp = null;

            string sql = "SELECT *, " +
                           "sort = CASE WHEN prims.UUID = prims.SceneGroupID THEN 0 ELSE 1 END " +
                           "FROM prims " +
                           "LEFT JOIN primshapes ON prims.UUID = primshapes.UUID " +
                           "WHERE RegionUUID = @RegionUUID " +
                           "ORDER BY SceneGroupID asc, sort asc, LinkNumber asc";

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand command = new SqlCommand(sql, conn))
            {
                command.Parameters.Add(_Database.CreateParameter("@regionUUID", regionUUID));
                conn.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        SceneObjectPart sceneObjectPart = BuildPrim(reader, scene);
                        if (reader["Shape"] is DBNull)
                            sceneObjectPart.Shape = PrimitiveBaseShape.Default;
                        else
                            sceneObjectPart.Shape = BuildShape(reader);

                        prims[sceneObjectPart.UUID] = sceneObjectPart;

                        UUID groupID = new UUID((Guid)reader["SceneGroupID"]);

                        if (groupID != lastGroupID) // New SOG
                        {
                            if (grp != null)
                                objects[grp.UUID] = grp;

                            lastGroupID = groupID;

                            // There sometimes exist OpenSim bugs that 'orphan groups' so that none of the prims are
                            // recorded as the root prim (for which the UUID must equal the persisted group UUID).  In
                            // this case, force the UUID to be the same as the group UUID so that at least these can be
                            // deleted (we need to change the UUID so that any other prims in the linkset can also be 
                            // deleted).
                            if (sceneObjectPart.UUID != groupID && groupID != UUID.Zero)
                            {
                                sceneObjectPart.UUID = groupID;
                            }

                            grp = new SceneObjectGroup(sceneObjectPart, scene);
                        }
                        else
                        {
                            grp.AddChild(sceneObjectPart, sceneObjectPart.LinkNum);
                        }
                    }
                }
            }

            if (grp != null)
                objects[grp.UUID] = grp;

            // Instead of attempting to LoadItems on every prim,
            // most of which probably have no items... get a 
            // list from DB of all prims which have items and
            // LoadItems only on those
            List<SceneObjectPart> primsWithInventory = new List<SceneObjectPart>();
            string qry = "select distinct primID from primitems";
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand command = new SqlCommand(qry, conn))
            {
                conn.Open();
                using (SqlDataReader itemReader = command.ExecuteReader())
                {
                    while (itemReader.Read())
                    {
                        if (!(itemReader["primID"] is DBNull))
                        {
                            UUID primID = new UUID(itemReader["primID"].ToString());
                            if (prims.ContainsKey(primID))
                            {
                                primsWithInventory.Add(prims[primID]);
                            }
                        }
                    }
                }
            }

            LoadItems(primsWithInventory);

            return new List<ISceneEntity> (objects.Values);
        }

        /// <summary>
        /// Load in the prim's persisted inventory.
        /// </summary>
        /// <param name="allPrims">all prims with inventory on a region</param>
        private void LoadItems(List<SceneObjectPart> allPrimsWithInventory)
        {
            string sql = "SELECT * FROM primitems WHERE PrimID = @PrimID";
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand command = new SqlCommand(sql, conn))
            {
                conn.Open();
                foreach (SceneObjectPart objectPart in allPrimsWithInventory)
                {
                    command.Parameters.Clear();
                    command.Parameters.Add(_Database.CreateParameter("@PrimID", objectPart.UUID));
                    
                    List<TaskInventoryItem> inventory = new List<TaskInventoryItem>();

                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            TaskInventoryItem item = BuildItem(reader);

                            item.ParentID = objectPart.UUID; // Values in database are
                            // often wrong
                            inventory.Add(item);
                        }
                    }

                    objectPart.Inventory.RestoreInventoryItems(inventory);
                }
            }
        }

        public void Tainted ()
        {
        }

        /// <summary>
        /// Stores all object's details apart from inventory
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="regionUUID"></param>
        public void StoreObject(SceneObjectGroup obj, UUID regionUUID)
        {
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            {
                conn.Open();
                SqlTransaction transaction = conn.BeginTransaction();

                try
                {
                    foreach (SceneObjectPart sceneObjectPart in obj.ChildrenList)
                    {
                        //Update prim
                        using (SqlCommand sqlCommand = conn.CreateCommand())
                        {
                            sqlCommand.Transaction = transaction;
                            try
                            {
                                StoreSceneObjectPrim(sceneObjectPart, sqlCommand, obj.UUID, regionUUID);
                            }
                            catch (SqlException)
                            {
                                throw;
                            }
                        }

                        //Update primshapes
                        using (SqlCommand sqlCommand = conn.CreateCommand())
                        {
                            sqlCommand.Transaction = transaction;
                            try
                            {
                                StoreSceneObjectPrimShapes(sceneObjectPart, sqlCommand, obj.UUID, regionUUID);
                            }
                            catch (SqlException)
                            {
                                throw;
                            }
                        }
                    }

                    transaction.Commit();
                }
                catch (Exception)
                {
                    try
                    {
                        transaction.Rollback();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Stores the prim of the sceneobjectpart.
        /// </summary>
        /// <param name="sceneObjectPart">The sceneobjectpart or prim.</param>
        /// <param name="sqlCommand">The SQL command with the transaction.</param>
        /// <param name="sceneGroupID">The scenegroup UUID.</param>
        /// <param name="regionUUID">The region UUID.</param>
        private void StoreSceneObjectPrim(SceneObjectPart sceneObjectPart, SqlCommand sqlCommand, UUID sceneGroupID, UUID regionUUID)
        {
            //Big query to update or insert a new prim.
            //Note for SQL Server 2008 this could be simplified
            string queryPrims = @"
IF EXISTS (SELECT UUID FROM prims WHERE UUID = @UUID)
    BEGIN
        UPDATE prims SET 
            CreationDate = @CreationDate, Name = @Name, Text = @Text, Description = @Description, SitName = @SitName, 
            TouchName = @TouchName, ObjectFlags = @ObjectFlags, OwnerMask = @OwnerMask, NextOwnerMask = @NextOwnerMask, GroupMask = @GroupMask, 
            EveryoneMask = @EveryoneMask, BaseMask = @BaseMask, PositionX = @PositionX, PositionY = @PositionY, PositionZ = @PositionZ, 
            GroupPositionX = @GroupPositionX, GroupPositionY = @GroupPositionY, GroupPositionZ = @GroupPositionZ, VelocityX = @VelocityX, 
            VelocityY = @VelocityY, VelocityZ = @VelocityZ, AngularVelocityX = @AngularVelocityX, AngularVelocityY = @AngularVelocityY, 
            AngularVelocityZ = @AngularVelocityZ, AccelerationX = @AccelerationX, AccelerationY = @AccelerationY, 
            AccelerationZ = @AccelerationZ, RotationX = @RotationX, RotationY = @RotationY, RotationZ = @RotationZ, RotationW = @RotationW, 
            SitTargetOffsetX = @SitTargetOffsetX, SitTargetOffsetY = @SitTargetOffsetY, SitTargetOffsetZ = @SitTargetOffsetZ, 
            SitTargetOrientW = @SitTargetOrientW, SitTargetOrientX = @SitTargetOrientX, SitTargetOrientY = @SitTargetOrientY, 
            SitTargetOrientZ = @SitTargetOrientZ, RegionUUID = @RegionUUID, CreatorID = @CreatorID, OwnerID = @OwnerID, GroupID = @GroupID, 
            LastOwnerID = @LastOwnerID, SceneGroupID = @SceneGroupID, PayPrice = @PayPrice, PayButton1 = @PayButton1, PayButton2 = @PayButton2, 
            PayButton3 = @PayButton3, PayButton4 = @PayButton4, LoopedSound = @LoopedSound, LoopedSoundGain = @LoopedSoundGain, 
            TextureAnimation = @TextureAnimation, OmegaX = @OmegaX, OmegaY = @OmegaY, OmegaZ = @OmegaZ, CameraEyeOffsetX = @CameraEyeOffsetX, 
            CameraEyeOffsetY = @CameraEyeOffsetY, CameraEyeOffsetZ = @CameraEyeOffsetZ, CameraAtOffsetX = @CameraAtOffsetX, 
            CameraAtOffsetY = @CameraAtOffsetY, CameraAtOffsetZ = @CameraAtOffsetZ, ForceMouselook = @ForceMouselook, 
            ScriptAccessPin = @ScriptAccessPin, AllowedDrop = @AllowedDrop, DieAtEdge = @DieAtEdge, SalePrice = @SalePrice, 
            SaleType = @SaleType, ColorR = @ColorR, ColorG = @ColorG, ColorB = @ColorB, ColorA = @ColorA, ParticleSystem = @ParticleSystem, 
            ClickAction = @ClickAction, Material = @Material, CollisionSound = @CollisionSound, CollisionSoundVolume = @CollisionSoundVolume, PassTouches = @PassTouches,
            LinkNumber = @LinkNumber, MediaURL = @MediaURL
        WHERE UUID = @UUID
    END
ELSE
    BEGIN
        INSERT INTO 
            prims (
            UUID, CreationDate, Name, Text, Description, SitName, TouchName, ObjectFlags, OwnerMask, NextOwnerMask, GroupMask, 
            EveryoneMask, BaseMask, PositionX, PositionY, PositionZ, GroupPositionX, GroupPositionY, GroupPositionZ, VelocityX, 
            VelocityY, VelocityZ, AngularVelocityX, AngularVelocityY, AngularVelocityZ, AccelerationX, AccelerationY, AccelerationZ, 
            RotationX, RotationY, RotationZ, RotationW, SitTargetOffsetX, SitTargetOffsetY, SitTargetOffsetZ, SitTargetOrientW, 
            SitTargetOrientX, SitTargetOrientY, SitTargetOrientZ, RegionUUID, CreatorID, OwnerID, GroupID, LastOwnerID, SceneGroupID, 
            PayPrice, PayButton1, PayButton2, PayButton3, PayButton4, LoopedSound, LoopedSoundGain, TextureAnimation, OmegaX, 
            OmegaY, OmegaZ, CameraEyeOffsetX, CameraEyeOffsetY, CameraEyeOffsetZ, CameraAtOffsetX, CameraAtOffsetY, CameraAtOffsetZ, 
            ForceMouselook, ScriptAccessPin, AllowedDrop, DieAtEdge, SalePrice, SaleType, ColorR, ColorG, ColorB, ColorA, 
            ParticleSystem, ClickAction, Material, CollisionSound, CollisionSoundVolume, PassTouches, LinkNumber, MediaURL, Generic
            ) VALUES (
            @UUID, @CreationDate, @Name, @Text, @Description, @SitName, @TouchName, @ObjectFlags, @OwnerMask, @NextOwnerMask, @GroupMask, 
            @EveryoneMask, @BaseMask, @PositionX, @PositionY, @PositionZ, @GroupPositionX, @GroupPositionY, @GroupPositionZ, @VelocityX, 
            @VelocityY, @VelocityZ, @AngularVelocityX, @AngularVelocityY, @AngularVelocityZ, @AccelerationX, @AccelerationY, @AccelerationZ, 
            @RotationX, @RotationY, @RotationZ, @RotationW, @SitTargetOffsetX, @SitTargetOffsetY, @SitTargetOffsetZ, @SitTargetOrientW, 
            @SitTargetOrientX, @SitTargetOrientY, @SitTargetOrientZ, @RegionUUID, @CreatorID, @OwnerID, @GroupID, @LastOwnerID, @SceneGroupID, 
            @PayPrice, @PayButton1, @PayButton2, @PayButton3, @PayButton4, @LoopedSound, @LoopedSoundGain, @TextureAnimation, @OmegaX, 
            @OmegaY, @OmegaZ, @CameraEyeOffsetX, @CameraEyeOffsetY, @CameraEyeOffsetZ, @CameraAtOffsetX, @CameraAtOffsetY, @CameraAtOffsetZ, 
            @ForceMouselook, @ScriptAccessPin, @AllowedDrop, @DieAtEdge, @SalePrice, @SaleType, @ColorR, @ColorG, @ColorB, @ColorA, 
            @ParticleSystem, @ClickAction, @Material, @CollisionSound, @CollisionSoundVolume, @PassTouches, @LinkNumber, @MediaURL, @Generic
            )
    END";

            //Set commandtext.
            sqlCommand.CommandText = queryPrims;
            //Add parameters
            sqlCommand.Parameters.AddRange(CreatePrimParameters(sceneObjectPart, sceneGroupID, regionUUID));

            //Execute the query. If it fails then error is trapped in calling function
            sqlCommand.ExecuteNonQuery();
        }

        /// <summary>
        /// Stores the scene object prim shapes.
        /// </summary>
        /// <param name="sceneObjectPart">The sceneobjectpart containing prim shape.</param>
        /// <param name="sqlCommand">The SQL command with the transaction.</param>
        /// <param name="sceneGroupID">The scenegroup UUID.</param>
        /// <param name="regionUUID">The region UUID.</param>
        private void StoreSceneObjectPrimShapes(SceneObjectPart sceneObjectPart, SqlCommand sqlCommand, UUID sceneGroupID, UUID regionUUID)
        {
            //Big query to or insert or update primshapes
            //Note for SQL Server 2008 this can be simplified
            string queryPrimShapes = @"
IF EXISTS (SELECT UUID FROM primshapes WHERE UUID = @UUID)
    BEGIN
        UPDATE primshapes SET 
            Shape = @Shape, ScaleX = @ScaleX, ScaleY = @ScaleY, ScaleZ = @ScaleZ, PCode = @PCode, PathBegin = @PathBegin, 
            PathEnd = @PathEnd, PathScaleX = @PathScaleX, PathScaleY = @PathScaleY, PathShearX = @PathShearX, PathShearY = @PathShearY, 
            PathSkew = @PathSkew, PathCurve = @PathCurve, PathRadiusOffset = @PathRadiusOffset, PathRevolutions = @PathRevolutions, 
            PathTaperX = @PathTaperX, PathTaperY = @PathTaperY, PathTwist = @PathTwist, PathTwistBegin = @PathTwistBegin, 
            ProfileBegin = @ProfileBegin, ProfileEnd = @ProfileEnd, ProfileCurve = @ProfileCurve, ProfileHollow = @ProfileHollow, 
            Texture = @Texture, ExtraParams = @ExtraParams, State = @State, Media = @Media
        WHERE UUID = @UUID
    END
ELSE
    BEGIN
        INSERT INTO 
            primshapes (
            UUID, Shape, ScaleX, ScaleY, ScaleZ, PCode, PathBegin, PathEnd, PathScaleX, PathScaleY, PathShearX, PathShearY, 
            PathSkew, PathCurve, PathRadiusOffset, PathRevolutions, PathTaperX, PathTaperY, PathTwist, PathTwistBegin, ProfileBegin, 
            ProfileEnd, ProfileCurve, ProfileHollow, Texture, ExtraParams, State, Media
            ) VALUES (
            @UUID, @Shape, @ScaleX, @ScaleY, @ScaleZ, @PCode, @PathBegin, @PathEnd, @PathScaleX, @PathScaleY, @PathShearX, @PathShearY, 
            @PathSkew, @PathCurve, @PathRadiusOffset, @PathRevolutions, @PathTaperX, @PathTaperY, @PathTwist, @PathTwistBegin, @ProfileBegin, 
            @ProfileEnd, @ProfileCurve, @ProfileHollow, @Texture, @ExtraParams, @State, @Media
            )
    END";

            //Set commandtext.
            sqlCommand.CommandText = queryPrimShapes;

            //Add parameters
            sqlCommand.Parameters.AddRange(CreatePrimShapeParameters(sceneObjectPart, sceneGroupID, regionUUID));

            //Execute the query. If it fails then error is trapped in calling function
            sqlCommand.ExecuteNonQuery();

        }

        /// <summary>
        /// Removes a object from the database.
        /// Meaning removing it from tables Prims, PrimShapes and PrimItems
        /// </summary>
        /// <param name="objectID">id of scenegroup</param>
        /// <param name="regionUUID">regionUUID (is this used anyway</param>
        public void RemoveObject(UUID objectID, UUID regionUUID)
        {
            //Remove from prims and primsitem table
            string sqlPrims = "DELETE FROM PRIMS WHERE SceneGroupID = @objectID";
            string sqlPrimItems = "DELETE FROM PRIMITEMS WHERE primID in (SELECT UUID FROM PRIMS WHERE SceneGroupID = @objectID)";
            string sqlPrimShapes = "DELETE FROM PRIMSHAPES WHERE uuid in (SELECT UUID FROM PRIMS WHERE SceneGroupID = @objectID)";

            lock (_Database)
            {
                //Using the non transaction mode.
                using (SqlConnection conn = new SqlConnection(m_connectionString))
                using (SqlCommand cmd = new SqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = sqlPrimShapes;
                    conn.Open();
                    cmd.Parameters.Add(_Database.CreateParameter("objectID", objectID));
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = sqlPrimItems;
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = sqlPrims;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void RemoveObjects(List<UUID> objGroups)
        {
            //This is extremely bad, but I don't know enough about MSSQL to fix
            //TODO: Add multiple deleting to MSSQL
            foreach (UUID objectID in objGroups)
            {
                //Remove from prims and primsitem table
                string sqlPrims = "DELETE FROM PRIMS WHERE SceneGroupID = @objectID";
                string sqlPrimItems = "DELETE FROM PRIMITEMS WHERE primID in (SELECT UUID FROM PRIMS WHERE SceneGroupID = @objectID)";
                string sqlPrimShapes = "DELETE FROM PRIMSHAPES WHERE uuid in (SELECT UUID FROM PRIMS WHERE SceneGroupID = @objectID)";

                lock (_Database)
                {
                    //Using the non transaction mode.
                    using (SqlConnection conn = new SqlConnection(m_connectionString))
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.CommandText = sqlPrimShapes;
                        conn.Open();
                        cmd.Parameters.Add(_Database.CreateParameter("objectID", objectID));
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = sqlPrimItems;
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = sqlPrims;
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Removes a object from the database.
        /// Meaning removing it from tables Prims, PrimShapes and PrimItems
        /// </summary>
        /// <param name="objectID">id of scenegroup</param>
        /// <param name="regionUUID">regionUUID (is this used anyway</param>
        public void RemoveRegion(UUID regionUUID)
        {
            //TODO: Remove from prims and primsitem table
            string sqlPrims = "DELETE FROM PRIMS WHERE RegionUUID = @regionID";
            
            lock (_Database)
            {
                //Using the non transaction mode.
                using (SqlConnection conn = new SqlConnection(m_connectionString))
                using (SqlCommand cmd = new SqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = sqlPrims;
                    cmd.ExecuteNonQuery();
                    conn.Open();
                    cmd.Parameters.Add(_Database.CreateParameter("regionID", regionUUID));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Store the inventory of a prim. Warning deletes everything first and then adds all again.
        /// </summary>
        /// <param name="primID"></param>
        /// <param name="items"></param>
        public void StorePrimInventory(UUID primID, ICollection<TaskInventoryItem> items)
        {
            //_Log.InfoFormat("[REGION DB: Persisting Prim Inventory with prim ID {0}", primID);

            //Statement from MySQL section!
            // For now, we're just going to crudely remove all the previous inventory items
            // no matter whether they have changed or not, and replace them with the current set.

            //Delete everything from PrimID
            //TODO add index on PrimID in DB, if not already exist

            string sql = "DELETE PRIMITEMS WHERE primID = @primID";
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@primID", primID));
                conn.Open();
                cmd.ExecuteNonQuery();
            }

            sql =
                @"INSERT INTO primitems (
            itemID,primID,assetID,parentFolderID,invType,assetType,name,description,creationDate,creatorID,ownerID,lastOwnerID,groupID,
            nextPermissions,currentPermissions,basePermissions,everyonePermissions,groupPermissions,flags) 
            VALUES (@itemID,@primID,@assetID,@parentFolderID,@invType,@assetType,@name,@description,@creationDate,@creatorID,@ownerID,
            @lastOwnerID,@groupID,@nextPermissions,@currentPermissions,@basePermissions,@everyonePermissions,@groupPermissions,@flags)";

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                foreach (TaskInventoryItem taskItem in items)
                {
                    cmd.Parameters.AddRange(CreatePrimInventoryParameters(taskItem));
                    conn.Open();
                    cmd.ExecuteNonQuery();

                    cmd.Parameters.Clear();
                }
            }
        }

        #endregion

        /// <summary>
        /// Loads the terrain map.
        /// </summary>
        /// <param name="regionID">regionID.</param>
        /// <returns></returns>
        public short[] LoadTerrain(IScene scene, bool Revert, int RegionSizeX, int RegionSizeY)
        {
            string sql = "select top 1 RegionUUID, Revision, Heightfield from terrain where RegionUUID = @RegionUUID and Revert = @Revert order by Revision desc";

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                // MySqlParameter param = new MySqlParameter();
                cmd.Parameters.Add(_Database.CreateParameter("@RegionUUID", scene.RegionInfo.RegionID));
                cmd.Parameters.Add(_Database.CreateParameter("@Revert", Revert));
                conn.Open();
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return (short[])reader["Heightfield"];
                    }
                    else
                    {
                        return null;
                    }
                    //_Log.Info("[REGION DB]: Loaded terrain revision r" + rev);
                }
            }
        }

        /// <summary>
        /// Loads the terrain map.
        /// </summary>
        /// <param name="regionID">regionID.</param>
        /// <returns></returns>
        public short[] LoadWater (IScene scene, bool Revert, int RegionSizeX, int RegionSizeY)
        {
            string sql = "select top 1 RegionUUID, Revision, Heightfield from terrain where RegionUUID = @RegionUUID and Revert = @Revert order by Revision desc";

            int r = Revert ? 3 : 2; //Use numbers so that we can coexist with terrain

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                // MySqlParameter param = new MySqlParameter();
                cmd.Parameters.Add (_Database.CreateParameter ("@RegionUUID", scene.RegionInfo.RegionID));
                cmd.Parameters.Add(_Database.CreateParameter("@Revert", r));
                conn.Open();
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return (short[])reader["Heightfield"];
                    }
                    else
                    {
                        return null;
                    }
                    //_Log.Info("[REGION DB]: Loaded terrain revision r" + rev);
                }
            }
        }

        /// <summary>
        /// Stores the terrain map to DB.
        /// </summary>
        /// <param name="terrain">terrain map data.</param>
        /// <param name="regionID">regionID.</param>
        public void StoreTerrain(short[] terrain, UUID regionID, bool Revert)
        {
            int revision = Util.UnixTimeSinceEpoch();

            //Delete old terrain map
            string sql = "delete from terrain where RegionUUID=@RegionUUID and Revert = @Revert";
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@RegionUUID", regionID));
                cmd.Parameters.Add(_Database.CreateParameter("@Revert", Revert.ToString()));
                conn.Open();
                cmd.ExecuteNonQuery();
            }

            sql = "insert into terrain(RegionUUID, Revision, Heightfield, Revert) values(@RegionUUID, @Revision, @Heightfield, @Revert)";

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@RegionUUID", regionID));
                cmd.Parameters.Add(_Database.CreateParameter("@Revision", revision));
                cmd.Parameters.Add(_Database.CreateParameter("@Heightfield", terrain));
                cmd.Parameters.Add(_Database.CreateParameter("@Revert", Revert.ToString()));
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Stores the terrain map to DB.
        /// </summary>
        /// <param name="terrain">terrain map data.</param>
        /// <param name="regionID">regionID.</param>
        public void StoreWater(short[] water, UUID regionID, bool Revert)
        {
            int revision = Util.UnixTimeSinceEpoch();

            int r = Revert ? 3 : 2; //Use numbers so that we can coexist with terrain

            //Delete old terrain map
            string sql = "delete from terrain where RegionUUID=@RegionUUID and Revert = @Revert";
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@RegionUUID", regionID));
                cmd.Parameters.Add(_Database.CreateParameter("@Revert", r));
                conn.Open();
                cmd.ExecuteNonQuery();
            }

            sql = "insert into terrain(RegionUUID, Revision, Heightfield, Revert) values(@RegionUUID, @Revision, @Heightfield, @Revert)";

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@RegionUUID", regionID));
                cmd.Parameters.Add(_Database.CreateParameter("@Revision", revision));
                cmd.Parameters.Add(_Database.CreateParameter("@Heightfield", water));
                cmd.Parameters.Add(_Database.CreateParameter("@Revert", r));
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Loads all the land objects of a region.
        /// </summary>
        /// <param name="regionUUID">The region UUID.</param>
        /// <returns></returns>
        public List<LandData> LoadLandObjects(UUID regionUUID)
        {
            List<LandData> LandDataForRegion = new List<LandData>();

            string sql = "select * from land where RegionUUID = @RegionUUID";

            //Retrieve all land data from region
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@RegionUUID", regionUUID));
                conn.Open();
                using (SqlDataReader readerLandData = cmd.ExecuteReader())
                {
                    while (readerLandData.Read())
                    {
                        LandDataForRegion.Add(BuildLandData(readerLandData));
                    }
                }
            }

            //Retrieve all accesslist data for all landdata
            foreach (LandData LandData in LandDataForRegion)
            {
                sql = "select * from landaccesslist where LandUUID = @LandUUID";
                using (SqlConnection conn = new SqlConnection(m_connectionString))
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add(_Database.CreateParameter("@LandUUID", LandData.GlobalID));
                    conn.Open();
                    using (SqlDataReader readerAccessList = cmd.ExecuteReader())
                    {
                        while (readerAccessList.Read())
                        {
                            LandData.ParcelAccessList.Add(BuildLandAccessData(readerAccessList));
                        }
                    }
                }
            }

            //Return data
            return LandDataForRegion;
        }

        /// <summary>
        /// Stores land object with landaccess list.
        /// </summary>
        /// <param name="parcel">parcel data.</param>
        public void StoreLandObject(LandData parcel)
        {
            //As this is only one record in land table I just delete all and then add a new record.
            //As the delete landaccess is already in the mysql code

            //Delete old values
            RemoveLandObject(parcel.RegionID, parcel.GlobalID);

            //Insert new values
            string sql = @"INSERT INTO [land] 
([UUID],[RegionUUID],[LocalLandID],[Bitmap],[Name],[Description],[OwnerUUID],[IsGroupOwned],[Area],[AuctionID],[Category],[ClaimDate],[ClaimPrice],[GroupUUID],[SalePrice],[LandStatus],[LandFlags],[LandingType],[MediaAutoScale],[MediaTextureUUID],[MediaURL],[MusicURL],[PassHours],[PassPrice],[SnapshotUUID],[UserLocationX],[UserLocationY],[UserLocationZ],[UserLookAtX],[UserLookAtY],[UserLookAtZ],[AuthbuyerID],[OtherCleanTime])
VALUES
(@UUID,@RegionUUID,@LocalLandID,@Bitmap,@Name,@Description,@OwnerUUID,@IsGroupOwned,@Area,@AuctionID,@Category,@ClaimDate,@ClaimPrice,@GroupUUID,@SalePrice,@LandStatus,@LandFlags,@LandingType,@MediaAutoScale,@MediaTextureUUID,@MediaURL,@MusicURL,@PassHours,@PassPrice,@SnapshotUUID,@UserLocationX,@UserLocationY,@UserLocationZ,@UserLookAtX,@UserLookAtY,@UserLookAtZ,@AuthbuyerID,@OtherCleanTime)";

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddRange(CreateLandParameters(parcel, parcel.RegionID));
                conn.Open();
                cmd.ExecuteNonQuery();
            }

            sql = "INSERT INTO [landaccesslist] ([LandUUID],[AccessUUID],[Flags]) VALUES (@LandUUID,@AccessUUID,@Flags)";

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                conn.Open();
                foreach (ParcelManager.ParcelAccessEntry parcelAccessEntry in parcel.ParcelAccessList)
                {
                    cmd.Parameters.AddRange(CreateLandAccessParameters(parcelAccessEntry, parcel.RegionID));

                    cmd.ExecuteNonQuery();
                    cmd.Parameters.Clear();
                }
            }
        }

        /// <summary>
        /// Removes a land object from DB.
        /// </summary>
        /// <param name="globalID">UUID of landobject</param>
        public void RemoveLandObject(UUID RegionID, UUID globalID)
        {
            string sql = "delete from land where UUID=@UUID";
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@UUID", globalID));
                conn.Open();
                cmd.ExecuteNonQuery();
            }
            sql = "delete from landaccesslist where LandUUID=@UUID";
            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@UUID", globalID));
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public void Shutdown()
        {
            //Not used??
        }

        #region Private Methods

        /// <summary>
        /// Stores new regionsettings.
        /// </summary>
        /// <param name="regionSettings">The region settings.</param>
        private void StoreNewRegionSettings(RegionSettings regionSettings)
        {
            string sql = @"INSERT INTO [regionsettings]
                                ([regionUUID],[block_terraform],[block_fly],[allow_damage],[restrict_pushing],[allow_land_resell],[allow_land_join_divide],
                                [block_show_in_search],[agent_limit],[object_bonus],[maturity],[disable_scripts],[disable_collisions],[disable_physics],
                                [terrain_texture_1],[terrain_texture_2],[terrain_texture_3],[terrain_texture_4],[elevation_1_nw],[elevation_2_nw],[elevation_1_ne],
                                [elevation_2_ne],[elevation_1_se],[elevation_2_se],[elevation_1_sw],[elevation_2_sw],[water_height],[terrain_raise_limit],
                                [terrain_lower_limit],[use_estate_sun],[fixed_sun],[sun_position],[covenant],[sunvectorx], [sunvectory], [sunvectorz],[Sandbox], [loaded_creation_datetime], [loaded_creation_id]
 ) 
                            VALUES
                                (@regionUUID,@block_terraform,@block_fly,@allow_damage,@restrict_pushing,@allow_land_resell,@allow_land_join_divide,
                                @block_show_in_search,@agent_limit,@object_bonus,@maturity,@disable_scripts,@disable_collisions,@disable_physics,
                                @terrain_texture_1,@terrain_texture_2,@terrain_texture_3,@terrain_texture_4,@elevation_1_nw,@elevation_2_nw,@elevation_1_ne,
                                @elevation_2_ne,@elevation_1_se,@elevation_2_se,@elevation_1_sw,@elevation_2_sw,@water_height,@terrain_raise_limit,
                                @terrain_lower_limit,@use_estate_sun,@fixed_sun,@sun_position,@covenant,@sunvectorx,@sunvectory, @sunvectorz, @Sandbox, @loaded_creation_datetime, @loaded_creation_id)";

            using (SqlConnection conn = new SqlConnection(m_connectionString))
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddRange(CreateRegionSettingParameters(regionSettings));
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        #region Private DataRecord conversion methods

        /// <summary>
        /// Builds the region settings from a datarecod.
        /// </summary>
        /// <param name="row">datarecord with regionsettings.</param>
        /// <returns></returns>
        private static RegionSettings BuildRegionSettings(IDataRecord row)
        {
            //TODO change this is some more generic code so we doesnt have to change it every time a new field is added?
            RegionSettings newSettings = new RegionSettings();

            newSettings.RegionUUID = new UUID((Guid)row["regionUUID"]);
            newSettings.BlockTerraform = Convert.ToBoolean(row["block_terraform"]);
            newSettings.AllowDamage = Convert.ToBoolean(row["allow_damage"]);
            newSettings.BlockFly = Convert.ToBoolean(row["block_fly"]);
            newSettings.RestrictPushing = Convert.ToBoolean(row["restrict_pushing"]);
            newSettings.AllowLandResell = Convert.ToBoolean(row["allow_land_resell"]);
            newSettings.AllowLandJoinDivide = Convert.ToBoolean(row["allow_land_join_divide"]);
            newSettings.BlockShowInSearch = Convert.ToBoolean(row["block_show_in_search"]);
            newSettings.AgentLimit = Convert.ToInt32(row["agent_limit"]);
            newSettings.ObjectBonus = Convert.ToDouble(row["object_bonus"]);
            newSettings.Maturity = Convert.ToInt32(row["maturity"]);
            newSettings.DisableScripts = Convert.ToBoolean(row["disable_scripts"]);
            newSettings.DisableCollisions = Convert.ToBoolean(row["disable_collisions"]);
            newSettings.DisablePhysics = Convert.ToBoolean(row["disable_physics"]);
            newSettings.TerrainTexture1 = new UUID((Guid)row["terrain_texture_1"]);
            newSettings.TerrainTexture2 = new UUID((Guid)row["terrain_texture_2"]);
            newSettings.TerrainTexture3 = new UUID((Guid)row["terrain_texture_3"]);
            newSettings.TerrainTexture4 = new UUID((Guid)row["terrain_texture_4"]);
            newSettings.Elevation1NW = Convert.ToDouble(row["elevation_1_nw"]);
            newSettings.Elevation2NW = Convert.ToDouble(row["elevation_2_nw"]);
            newSettings.Elevation1NE = Convert.ToDouble(row["elevation_1_ne"]);
            newSettings.Elevation2NE = Convert.ToDouble(row["elevation_2_ne"]);
            newSettings.Elevation1SE = Convert.ToDouble(row["elevation_1_se"]);
            newSettings.Elevation2SE = Convert.ToDouble(row["elevation_2_se"]);
            newSettings.Elevation1SW = Convert.ToDouble(row["elevation_1_sw"]);
            newSettings.Elevation2SW = Convert.ToDouble(row["elevation_2_sw"]);
            newSettings.WaterHeight = Convert.ToDouble(row["water_height"]);
            newSettings.TerrainRaiseLimit = Convert.ToDouble(row["terrain_raise_limit"]);
            newSettings.TerrainLowerLimit = Convert.ToDouble(row["terrain_lower_limit"]);
            newSettings.UseEstateSun = Convert.ToBoolean(row["use_estate_sun"]);
            newSettings.Sandbox = Convert.ToBoolean(row["sandbox"]);
            newSettings.FixedSun = Convert.ToBoolean(row["fixed_sun"]);
            newSettings.SunPosition = Convert.ToDouble(row["sun_position"]);
            newSettings.SunVector = new Vector3(
                                                 Convert.ToSingle(row["sunvectorx"]),
                                                 Convert.ToSingle(row["sunvectory"]),
                                                 Convert.ToSingle(row["sunvectorz"])
                                                 );
            newSettings.Covenant = new UUID((Guid)row["covenant"]);
            newSettings.CovenantLastUpdated = Convert.ToInt32(row["covenantlastupdated"]);
            newSettings.MinimumAge = Convert.ToInt32(row["minimum_age"]);

            newSettings.LoadedCreationDateTime = Convert.ToInt32(row["loaded_creation_datetime"]);

            if (row["loaded_creation_id"] is DBNull)
                newSettings.LoadedCreationID = "";
            else
                newSettings.LoadedCreationID = (String)row["loaded_creation_id"];

            OSD o = OSDParser.DeserializeJson((String)row["generic"]);
            if (o.Type == OSDType.Map)
                newSettings.Generic = (OSDMap)o;
            return newSettings;
        }

        /// <summary>
        /// Builds the land data from a datarecord.
        /// </summary>
        /// <param name="row">datarecord with land data</param>
        /// <returns></returns>
        private static LandData BuildLandData(IDataRecord row)
        {
            LandData newData = new LandData();

            newData.GlobalID = new UUID((Guid)row["UUID"]);
            newData.LocalID = Convert.ToInt32(row["LocalLandID"]);

            // Bitmap is a byte[512]
            newData.Bitmap = (Byte[])row["Bitmap"];

            newData.Name = (string)row["Name"];
            newData.Description = (string)row["Description"];
            newData.OwnerID = new UUID((Guid)row["OwnerUUID"]);
            newData.IsGroupOwned = Convert.ToBoolean(row["IsGroupOwned"]);
            newData.Area = Convert.ToInt32(row["Area"]);
            newData.AuctionID = Convert.ToUInt32(row["AuctionID"]); //Unemplemented
            newData.Category = (ParcelCategory)Convert.ToInt32(row["Category"]);
            //Enum libsecondlife.Parcel.ParcelCategory
            newData.ClaimDate = Convert.ToInt32(row["ClaimDate"]);
            newData.ClaimPrice = Convert.ToInt32(row["ClaimPrice"]);
            newData.GroupID = new UUID((Guid)row["GroupUUID"]);
            newData.SalePrice = Convert.ToInt32(row["SalePrice"]);
            newData.Status = (ParcelStatus)Convert.ToInt32(row["LandStatus"]);
            //Enum. libsecondlife.Parcel.ParcelStatus
            newData.Flags = Convert.ToUInt32(row["LandFlags"]);
            newData.LandingType = Convert.ToByte(row["LandingType"]);
            newData.MediaAutoScale = Convert.ToByte(row["MediaAutoScale"]);
            newData.MediaID = new UUID((Guid)row["MediaTextureUUID"]);
            newData.MediaURL = (string)row["MediaURL"];
            newData.MusicURL = (string)row["MusicURL"];
            newData.PassHours = Convert.ToSingle(row["PassHours"]);
            newData.PassPrice = Convert.ToInt32(row["PassPrice"]);

            //            UUID authedbuyer;
            //            UUID snapshotID;
            //
            //            if (UUID.TryParse((string)row["AuthBuyerID"], out authedbuyer))
            //                newData.AuthBuyerID = authedbuyer;
            //
            //            if (UUID.TryParse((string)row["SnapshotUUID"], out snapshotID))
            //                newData.SnapshotID = snapshotID;
            newData.AuthBuyerID = new UUID((Guid)row["AuthBuyerID"]);
            newData.SnapshotID = new UUID((Guid)row["SnapshotUUID"]);

            newData.OtherCleanTime = Convert.ToInt32(row["OtherCleanTime"]);

            try
            {
                newData.UserLocation =
                    new Vector3(Convert.ToSingle(row["UserLocationX"]), Convert.ToSingle(row["UserLocationY"]),
                                  Convert.ToSingle(row["UserLocationZ"]));
                newData.UserLookAt =
                    new Vector3(Convert.ToSingle(row["UserLookAtX"]), Convert.ToSingle(row["UserLookAtY"]),
                                  Convert.ToSingle(row["UserLookAtZ"]));
            }
            catch (InvalidCastException)
            {
                newData.UserLocation = Vector3.Zero;
                newData.UserLookAt = Vector3.Zero;
            }

            newData.ParcelAccessList = new List<ParcelManager.ParcelAccessEntry>();

            return newData;
        }

        /// <summary>
        /// Builds the landaccess data from a data record.
        /// </summary>
        /// <param name="row">datarecord with landaccess data</param>
        /// <returns></returns>
        private static ParcelManager.ParcelAccessEntry BuildLandAccessData(IDataRecord row)
        {
            ParcelManager.ParcelAccessEntry entry = new ParcelManager.ParcelAccessEntry();
            entry.AgentID = new UUID((Guid)row["AccessUUID"]);
            entry.Flags = (AccessList)Convert.ToInt32(row["Flags"]);
            entry.Time = new DateTime();
            return entry;
        }

        /// <summary>
        /// Builds the prim from a datarecord.
        /// </summary>
        /// <param name="primRow">datarecord</param>
        /// <returns></returns>
        private static SceneObjectPart BuildPrim(IDataRecord primRow, IScene scene)
        {
            SceneObjectPart prim = new SceneObjectPart(scene);

            prim.UUID = new UUID((Guid)primRow["UUID"]);
            // explicit conversion of integers is required, which sort
            // of sucks.  No idea if there is a shortcut here or not.
            prim.CreationDate = Convert.ToInt32(primRow["CreationDate"]);
            prim.Name = (string)primRow["Name"];
            // various text fields
            prim.Text = (string)primRow["Text"];
            prim.Color = Color.FromArgb(Convert.ToInt32(primRow["ColorA"]),
                                        Convert.ToInt32(primRow["ColorR"]),
                                        Convert.ToInt32(primRow["ColorG"]),
                                        Convert.ToInt32(primRow["ColorB"]));
            prim.Description = (string)primRow["Description"];
            prim.SitName = (string)primRow["SitName"];
            prim.TouchName = (string)primRow["TouchName"];
            // permissions
            prim.Flags = (PrimFlags)Convert.ToUInt32(primRow["ObjectFlags"]);
            prim.CreatorID = new UUID((Guid)primRow["CreatorID"]);
            prim.OwnerID = new UUID((Guid)primRow["OwnerID"]);
            prim.GroupID = new UUID((Guid)primRow["GroupID"]);
            prim.LastOwnerID = new UUID((Guid)primRow["LastOwnerID"]);
            prim.OwnerMask = Convert.ToUInt32(primRow["OwnerMask"]);
            prim.NextOwnerMask = Convert.ToUInt32(primRow["NextOwnerMask"]);
            prim.GroupMask = Convert.ToUInt32(primRow["GroupMask"]);
            prim.EveryoneMask = Convert.ToUInt32(primRow["EveryoneMask"]);
            prim.BaseMask = Convert.ToUInt32(primRow["BaseMask"]);
            // vectors
            prim.FixOffsetPosition ( new Vector3(
                                    Convert.ToSingle(primRow["PositionX"]),
                                    Convert.ToSingle(primRow["PositionY"]),
                                    Convert.ToSingle(primRow["PositionZ"]))
                                    ,true);

            prim.FixGroupPosition (new Vector3(
                                    Convert.ToSingle(primRow["GroupPositionX"]),
                                    Convert.ToSingle(primRow["GroupPositionY"]),
                                    Convert.ToSingle(primRow["GroupPositionZ"]))
                                    ,true);

            prim.Velocity = new Vector3(
                                Convert.ToSingle(primRow["VelocityX"]),
                                Convert.ToSingle(primRow["VelocityY"]),
                                Convert.ToSingle(primRow["VelocityZ"]));

            prim.AngularVelocity = new Vector3(
                                    Convert.ToSingle(primRow["AngularVelocityX"]),
                                    Convert.ToSingle(primRow["AngularVelocityY"]),
                                    Convert.ToSingle(primRow["AngularVelocityZ"]));

            prim.Acceleration = new Vector3(
                                Convert.ToSingle(primRow["AccelerationX"]),
                                Convert.ToSingle(primRow["AccelerationY"]),
                                Convert.ToSingle(primRow["AccelerationZ"]));

            // quaternions
            prim.RotationOffset = new Quaternion(
                                Convert.ToSingle(primRow["RotationX"]),
                                Convert.ToSingle(primRow["RotationY"]),
                                Convert.ToSingle(primRow["RotationZ"]),
                                Convert.ToSingle(primRow["RotationW"]));

            prim.SitTargetPositionLL = new Vector3(
                                Convert.ToSingle(primRow["SitTargetOffsetX"]),
                                Convert.ToSingle(primRow["SitTargetOffsetY"]),
                                Convert.ToSingle(primRow["SitTargetOffsetZ"]));

            prim.SitTargetOrientationLL = new Quaternion(
                                Convert.ToSingle(primRow["SitTargetOrientX"]),
                                Convert.ToSingle(primRow["SitTargetOrientY"]),
                                Convert.ToSingle(primRow["SitTargetOrientZ"]),
                                Convert.ToSingle(primRow["SitTargetOrientW"]));

            prim.PayPrice[0] = Convert.ToInt32(primRow["PayPrice"]);
            prim.PayPrice[1] = Convert.ToInt32(primRow["PayButton1"]);
            prim.PayPrice[2] = Convert.ToInt32(primRow["PayButton2"]);
            prim.PayPrice[3] = Convert.ToInt32(primRow["PayButton3"]);
            prim.PayPrice[4] = Convert.ToInt32(primRow["PayButton4"]);

            prim.Sound = new UUID((Guid)primRow["LoopedSound"]);
            prim.SoundGain = Convert.ToSingle(primRow["LoopedSoundGain"]);
            prim.SoundFlags = 1; // If it's persisted at all, it's looped

            if (!(primRow["TextureAnimation"] is DBNull))
                prim.TextureAnimation = (Byte[])primRow["TextureAnimation"];
            if (!(primRow["ParticleSystem"] is DBNull))
                prim.ParticleSystem = (Byte[])primRow["ParticleSystem"];

            prim.AngularVelocity = new Vector3(
                                        Convert.ToSingle(primRow["OmegaX"]),
                                        Convert.ToSingle(primRow["OmegaY"]),
                                        Convert.ToSingle(primRow["OmegaZ"]));

            prim.CameraEyeOffset = new Vector3(
                                        Convert.ToSingle(primRow["CameraEyeOffsetX"]),
                                        Convert.ToSingle(primRow["CameraEyeOffsetY"]),
                                        Convert.ToSingle(primRow["CameraEyeOffsetZ"])
                                        );

            prim.CameraAtOffset = new Vector3(
                                       Convert.ToSingle(primRow["CameraAtOffsetX"]),
                                       Convert.ToSingle(primRow["CameraAtOffsetY"]),
                                       Convert.ToSingle(primRow["CameraAtOffsetZ"])
                                       );

            if (Convert.ToInt16(primRow["ForceMouselook"]) != 0)
                prim.ForceMouselook = (true);

            prim.ScriptAccessPin = Convert.ToInt32(primRow["ScriptAccessPin"]);

            if (Convert.ToInt16(primRow["AllowedDrop"]) != 0)
                prim.AllowedDrop = true;

            if (Convert.ToInt16(primRow["DieAtEdge"]) != 0)
                prim.DIE_AT_EDGE = true;

            prim.SalePrice = Convert.ToInt32(primRow["SalePrice"]);
            prim.ObjectSaleType = Convert.ToByte(primRow["SaleType"]);

            prim.Material = Convert.ToByte(primRow["Material"]);

            if (!(primRow["ClickAction"] is DBNull))
                prim.ClickAction = Convert.ToByte(primRow["ClickAction"]);

            prim.CollisionSound = new UUID((Guid)primRow["CollisionSound"]);
            prim.CollisionSoundVolume = Convert.ToSingle(primRow["CollisionSoundVolume"]);
            prim.PassTouch = Convert.ToInt32(primRow["PassTouches"]);
            prim.LinkNum = Convert.ToInt32(primRow["LinkNumber"]);
            prim.GenericData = (string)primRow["Generic"];
            if (!(primRow["MediaURL"] is System.DBNull))
                prim.MediaUrl = (string)primRow["MediaURL"];

            return prim;
        }

        /// <summary>
        /// Builds the prim shape from a datarecord.
        /// </summary>
        /// <param name="shapeRow">The row.</param>
        /// <returns></returns>
        private static PrimitiveBaseShape BuildShape(IDataRecord shapeRow)
        {
            PrimitiveBaseShape baseShape = new PrimitiveBaseShape();

            baseShape.Scale = new Vector3(
                        Convert.ToSingle(shapeRow["ScaleX"]),
                        Convert.ToSingle(shapeRow["ScaleY"]),
                        Convert.ToSingle(shapeRow["ScaleZ"]));

            // paths
            baseShape.PCode = Convert.ToByte(shapeRow["PCode"]);
            baseShape.PathBegin = Convert.ToUInt16(shapeRow["PathBegin"]);
            baseShape.PathEnd = Convert.ToUInt16(shapeRow["PathEnd"]);
            baseShape.PathScaleX = Convert.ToByte(shapeRow["PathScaleX"]);
            baseShape.PathScaleY = Convert.ToByte(shapeRow["PathScaleY"]);
            baseShape.PathShearX = Convert.ToByte(shapeRow["PathShearX"]);
            baseShape.PathShearY = Convert.ToByte(shapeRow["PathShearY"]);
            baseShape.PathSkew = Convert.ToSByte(shapeRow["PathSkew"]);
            baseShape.PathCurve = Convert.ToByte(shapeRow["PathCurve"]);
            baseShape.PathRadiusOffset = Convert.ToSByte(shapeRow["PathRadiusOffset"]);
            baseShape.PathRevolutions = Convert.ToByte(shapeRow["PathRevolutions"]);
            baseShape.PathTaperX = Convert.ToSByte(shapeRow["PathTaperX"]);
            baseShape.PathTaperY = Convert.ToSByte(shapeRow["PathTaperY"]);
            baseShape.PathTwist = Convert.ToSByte(shapeRow["PathTwist"]);
            baseShape.PathTwistBegin = Convert.ToSByte(shapeRow["PathTwistBegin"]);
            // profile
            baseShape.ProfileBegin = Convert.ToUInt16(shapeRow["ProfileBegin"]);
            baseShape.ProfileEnd = Convert.ToUInt16(shapeRow["ProfileEnd"]);
            baseShape.ProfileCurve = Convert.ToByte(shapeRow["ProfileCurve"]);
            baseShape.ProfileHollow = Convert.ToUInt16(shapeRow["ProfileHollow"]);

            byte[] textureEntry = (byte[])shapeRow["Texture"];
            baseShape.TextureEntry = textureEntry;

            baseShape.ExtraParams = (byte[])shapeRow["ExtraParams"];

            try
            {
                baseShape.State = Convert.ToByte(shapeRow["State"]);
            }
            catch (InvalidCastException)
            {
            }

            if (!(shapeRow["Media"] is System.DBNull))
                baseShape.Media = PrimitiveBaseShape.MediaList.FromXml((string)shapeRow["Media"]);

            return baseShape;
        }

        /// <summary>
        /// Build a prim inventory item from the persisted data.
        /// </summary>
        /// <param name="inventoryRow"></param>
        /// <returns></returns>
        private static TaskInventoryItem BuildItem(IDataRecord inventoryRow)
        {
            TaskInventoryItem taskItem = new TaskInventoryItem();

            taskItem.ItemID = new UUID((Guid)inventoryRow["itemID"]);
            taskItem.ParentPartID = new UUID((Guid)inventoryRow["primID"]);
            taskItem.AssetID = new UUID((Guid)inventoryRow["assetID"]);
            taskItem.ParentID = new UUID((Guid)inventoryRow["parentFolderID"]);

            taskItem.InvType = Convert.ToInt32(inventoryRow["invType"]);
            taskItem.Type = Convert.ToInt32(inventoryRow["assetType"]);

            taskItem.Name = (string)inventoryRow["name"];
            taskItem.Description = (string)inventoryRow["description"];
            taskItem.CreationDate = Convert.ToUInt32(inventoryRow["creationDate"]);
            taskItem.CreatorID = new UUID((Guid)inventoryRow["creatorID"]);
            taskItem.OwnerID = new UUID((Guid)inventoryRow["ownerID"]);
            taskItem.LastOwnerID = new UUID((Guid)inventoryRow["lastOwnerID"]);
            taskItem.GroupID = new UUID((Guid)inventoryRow["groupID"]);

            taskItem.NextPermissions = Convert.ToUInt32(inventoryRow["nextPermissions"]);
            taskItem.CurrentPermissions = Convert.ToUInt32(inventoryRow["currentPermissions"]);
            taskItem.BasePermissions = Convert.ToUInt32(inventoryRow["basePermissions"]);
            taskItem.EveryonePermissions = Convert.ToUInt32(inventoryRow["everyonePermissions"]);
            taskItem.GroupPermissions = Convert.ToUInt32(inventoryRow["groupPermissions"]);
            taskItem.Flags = Convert.ToUInt32(inventoryRow["flags"]);
            taskItem.SalePrice = Convert.ToInt32(inventoryRow["salePrice"]);
            taskItem.SaleType = Convert.ToByte(inventoryRow["saleType"]);

            return taskItem;
        }

        #endregion

        #region Create parameters methods

        /// <summary>
        /// Creates the prim inventory parameters.
        /// </summary>
        /// <param name="taskItem">item in inventory.</param>
        /// <returns></returns>
        private SqlParameter[] CreatePrimInventoryParameters(TaskInventoryItem taskItem)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("itemID", taskItem.ItemID));
            parameters.Add(_Database.CreateParameter("primID", taskItem.ParentPartID));
            parameters.Add(_Database.CreateParameter("assetID", taskItem.AssetID));
            parameters.Add(_Database.CreateParameter("parentFolderID", taskItem.ParentID));
            parameters.Add(_Database.CreateParameter("invType", taskItem.InvType));
            parameters.Add(_Database.CreateParameter("assetType", taskItem.Type));

            parameters.Add(_Database.CreateParameter("name", taskItem.Name));
            parameters.Add(_Database.CreateParameter("description", taskItem.Description));
            parameters.Add(_Database.CreateParameter("creationDate", taskItem.CreationDate));
            parameters.Add(_Database.CreateParameter("creatorID", taskItem.CreatorID));
            parameters.Add(_Database.CreateParameter("ownerID", taskItem.OwnerID));
            parameters.Add(_Database.CreateParameter("lastOwnerID", taskItem.LastOwnerID));
            parameters.Add(_Database.CreateParameter("groupID", taskItem.GroupID));
            parameters.Add(_Database.CreateParameter("nextPermissions", taskItem.NextPermissions));
            parameters.Add(_Database.CreateParameter("currentPermissions", taskItem.CurrentPermissions));
            parameters.Add(_Database.CreateParameter("basePermissions", taskItem.BasePermissions));
            parameters.Add(_Database.CreateParameter("everyonePermissions", taskItem.EveryonePermissions));
            parameters.Add(_Database.CreateParameter("groupPermissions", taskItem.GroupPermissions));
            parameters.Add(_Database.CreateParameter("flags", taskItem.Flags));
            parameters.Add(_Database.CreateParameter("salePrice", taskItem.SalePrice));
            parameters.Add(_Database.CreateParameter("saleType", taskItem.SaleType));

            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the region setting parameters.
        /// </summary>
        /// <param name="settings">regionsettings.</param>
        /// <returns></returns>
        private SqlParameter[] CreateRegionSettingParameters(RegionSettings settings)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("regionUUID", settings.RegionUUID));
            parameters.Add(_Database.CreateParameter("block_terraform", settings.BlockTerraform));
            parameters.Add(_Database.CreateParameter("block_fly", settings.BlockFly));
            parameters.Add(_Database.CreateParameter("allow_damage", settings.AllowDamage));
            parameters.Add(_Database.CreateParameter("restrict_pushing", settings.RestrictPushing));
            parameters.Add(_Database.CreateParameter("allow_land_resell", settings.AllowLandResell));
            parameters.Add(_Database.CreateParameter("allow_land_join_divide", settings.AllowLandJoinDivide));
            parameters.Add(_Database.CreateParameter("block_show_in_search", settings.BlockShowInSearch));
            parameters.Add(_Database.CreateParameter("agent_limit", settings.AgentLimit));
            parameters.Add(_Database.CreateParameter("object_bonus", settings.ObjectBonus));
            parameters.Add(_Database.CreateParameter("maturity", settings.Maturity));
            parameters.Add(_Database.CreateParameter("disable_scripts", settings.DisableScripts));
            parameters.Add(_Database.CreateParameter("disable_collisions", settings.DisableCollisions));
            parameters.Add(_Database.CreateParameter("disable_physics", settings.DisablePhysics));
            parameters.Add(_Database.CreateParameter("terrain_texture_1", settings.TerrainTexture1));
            parameters.Add(_Database.CreateParameter("terrain_texture_2", settings.TerrainTexture2));
            parameters.Add(_Database.CreateParameter("terrain_texture_3", settings.TerrainTexture3));
            parameters.Add(_Database.CreateParameter("terrain_texture_4", settings.TerrainTexture4));
            parameters.Add(_Database.CreateParameter("elevation_1_nw", settings.Elevation1NW));
            parameters.Add(_Database.CreateParameter("elevation_2_nw", settings.Elevation2NW));
            parameters.Add(_Database.CreateParameter("elevation_1_ne", settings.Elevation1NE));
            parameters.Add(_Database.CreateParameter("elevation_2_ne", settings.Elevation2NE));
            parameters.Add(_Database.CreateParameter("elevation_1_se", settings.Elevation1SE));
            parameters.Add(_Database.CreateParameter("elevation_2_se", settings.Elevation2SE));
            parameters.Add(_Database.CreateParameter("elevation_1_sw", settings.Elevation1SW));
            parameters.Add(_Database.CreateParameter("elevation_2_sw", settings.Elevation2SW));
            parameters.Add(_Database.CreateParameter("water_height", settings.WaterHeight));
            parameters.Add(_Database.CreateParameter("terrain_raise_limit", settings.TerrainRaiseLimit));
            parameters.Add(_Database.CreateParameter("terrain_lower_limit", settings.TerrainLowerLimit));
            parameters.Add(_Database.CreateParameter("use_estate_sun", settings.UseEstateSun));
            parameters.Add(_Database.CreateParameter("sandbox", settings.Sandbox));
            parameters.Add(_Database.CreateParameter("fixed_sun", settings.FixedSun));
            parameters.Add(_Database.CreateParameter("sun_position", settings.SunPosition));
            parameters.Add(_Database.CreateParameter("sunvectorx", settings.SunVector.X));
            parameters.Add(_Database.CreateParameter("sunvectory", settings.SunVector.Y));
            parameters.Add(_Database.CreateParameter("sunvectorz", settings.SunVector.Z));
            parameters.Add(_Database.CreateParameter("covenant", settings.Covenant));
            parameters.Add(_Database.CreateParameter("covenantlastupdated", settings.CovenantLastUpdated));
            parameters.Add(_Database.CreateParameter("Loaded_Creation_DateTime", settings.LoadedCreationDateTime));
            parameters.Add(_Database.CreateParameter("Loaded_Creation_ID", settings.LoadedCreationID));
            parameters.Add(_Database.CreateParameter("minimum_age", settings.LoadedCreationDateTime));
            parameters.Add(_Database.CreateParameter("generic", OSDParser.SerializeJsonString(settings.Generic)));
            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the land parameters.
        /// </summary>
        /// <param name="land">land parameters.</param>
        /// <param name="regionUUID">region UUID.</param>
        /// <returns></returns>
        private SqlParameter[] CreateLandParameters(LandData land, UUID regionUUID)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("UUID", land.GlobalID));
            parameters.Add(_Database.CreateParameter("RegionUUID", regionUUID));
            parameters.Add(_Database.CreateParameter("LocalLandID", land.LocalID));

            // Bitmap is a byte[512]
            parameters.Add(_Database.CreateParameter("Bitmap", land.Bitmap));

            parameters.Add(_Database.CreateParameter("Name", land.Name));
            parameters.Add(_Database.CreateParameter("Description", land.Description));
            parameters.Add(_Database.CreateParameter("OwnerUUID", land.OwnerID));
            parameters.Add(_Database.CreateParameter("IsGroupOwned", land.IsGroupOwned));
            parameters.Add(_Database.CreateParameter("Area", land.Area));
            parameters.Add(_Database.CreateParameter("AuctionID", land.AuctionID)); //Unemplemented
            parameters.Add(_Database.CreateParameter("Category", (int)land.Category)); //Enum libsecondlife.Parcel.ParcelCategory
            parameters.Add(_Database.CreateParameter("ClaimDate", land.ClaimDate));
            parameters.Add(_Database.CreateParameter("ClaimPrice", land.ClaimPrice));
            parameters.Add(_Database.CreateParameter("GroupUUID", land.GroupID));
            parameters.Add(_Database.CreateParameter("SalePrice", land.SalePrice));
            parameters.Add(_Database.CreateParameter("LandStatus", (int)land.Status)); //Enum. libsecondlife.Parcel.ParcelStatus
            parameters.Add(_Database.CreateParameter("LandFlags", land.Flags));
            parameters.Add(_Database.CreateParameter("LandingType", land.LandingType));
            parameters.Add(_Database.CreateParameter("MediaAutoScale", land.MediaAutoScale));
            parameters.Add(_Database.CreateParameter("MediaTextureUUID", land.MediaID));
            parameters.Add(_Database.CreateParameter("MediaURL", land.MediaURL));
            parameters.Add(_Database.CreateParameter("MusicURL", land.MusicURL));
            parameters.Add(_Database.CreateParameter("PassHours", land.PassHours));
            parameters.Add(_Database.CreateParameter("PassPrice", land.PassPrice));
            parameters.Add(_Database.CreateParameter("SnapshotUUID", land.SnapshotID));
            parameters.Add(_Database.CreateParameter("UserLocationX", land.UserLocation.X));
            parameters.Add(_Database.CreateParameter("UserLocationY", land.UserLocation.Y));
            parameters.Add(_Database.CreateParameter("UserLocationZ", land.UserLocation.Z));
            parameters.Add(_Database.CreateParameter("UserLookAtX", land.UserLookAt.X));
            parameters.Add(_Database.CreateParameter("UserLookAtY", land.UserLookAt.Y));
            parameters.Add(_Database.CreateParameter("UserLookAtZ", land.UserLookAt.Z));
            parameters.Add(_Database.CreateParameter("AuthBuyerID", land.AuthBuyerID));
            parameters.Add(_Database.CreateParameter("OtherCleanTime", land.OtherCleanTime));

            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the land access parameters.
        /// </summary>
        /// <param name="parcelAccessEntry">parcel access entry.</param>
        /// <param name="parcelID">parcel ID.</param>
        /// <returns></returns>
        private SqlParameter[] CreateLandAccessParameters(ParcelManager.ParcelAccessEntry parcelAccessEntry, UUID parcelID)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("LandUUID", parcelID));
            parameters.Add(_Database.CreateParameter("AccessUUID", parcelAccessEntry.AgentID));
            parameters.Add(_Database.CreateParameter("Flags", parcelAccessEntry.Flags));

            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the prim parameters for storing in DB.
        /// </summary>
        /// <param name="prim">Basic data of SceneObjectpart prim.</param>
        /// <param name="sceneGroupID">The scenegroup ID.</param>
        /// <param name="regionUUID">The region ID.</param>
        /// <returns></returns>
        private SqlParameter[] CreatePrimParameters(SceneObjectPart prim, UUID sceneGroupID, UUID regionUUID)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("UUID", prim.UUID));
            parameters.Add(_Database.CreateParameter("RegionUUID", regionUUID));
            parameters.Add(_Database.CreateParameter("CreationDate", prim.CreationDate));
            parameters.Add(_Database.CreateParameter("Name", prim.Name));
            parameters.Add(_Database.CreateParameter("SceneGroupID", sceneGroupID));
            // the UUID of the root part for this SceneObjectGroup
            // various text fields
            parameters.Add(_Database.CreateParameter("Text", prim.Text));
            parameters.Add(_Database.CreateParameter("ColorR", prim.Color.R));
            parameters.Add(_Database.CreateParameter("ColorG", prim.Color.G));
            parameters.Add(_Database.CreateParameter("ColorB", prim.Color.B));
            parameters.Add(_Database.CreateParameter("ColorA", prim.Color.A));
            parameters.Add(_Database.CreateParameter("Description", prim.Description));
            parameters.Add(_Database.CreateParameter("SitName", prim.SitName));
            parameters.Add(_Database.CreateParameter("TouchName", prim.TouchName));
            // permissions
            parameters.Add(_Database.CreateParameter("ObjectFlags", (uint)prim.Flags));
            parameters.Add(_Database.CreateParameter("CreatorID", prim.CreatorID));
            parameters.Add(_Database.CreateParameter("OwnerID", prim.OwnerID));
            parameters.Add(_Database.CreateParameter("GroupID", prim.GroupID));
            parameters.Add(_Database.CreateParameter("LastOwnerID", prim.LastOwnerID));
            parameters.Add(_Database.CreateParameter("OwnerMask", prim.OwnerMask));
            parameters.Add(_Database.CreateParameter("NextOwnerMask", prim.NextOwnerMask));
            parameters.Add(_Database.CreateParameter("GroupMask", prim.GroupMask));
            parameters.Add(_Database.CreateParameter("EveryoneMask", prim.EveryoneMask));
            parameters.Add(_Database.CreateParameter("BaseMask", prim.BaseMask));
            // vectors
            parameters.Add(_Database.CreateParameter("PositionX", prim.OffsetPosition.X));
            parameters.Add(_Database.CreateParameter("PositionY", prim.OffsetPosition.Y));
            parameters.Add(_Database.CreateParameter("PositionZ", prim.OffsetPosition.Z));
            parameters.Add(_Database.CreateParameter("GroupPositionX", prim.GroupPosition.X));
            parameters.Add(_Database.CreateParameter("GroupPositionY", prim.GroupPosition.Y));
            parameters.Add(_Database.CreateParameter("GroupPositionZ", prim.GroupPosition.Z));
            parameters.Add(_Database.CreateParameter("VelocityX", prim.Velocity.X));
            parameters.Add(_Database.CreateParameter("VelocityY", prim.Velocity.Y));
            parameters.Add(_Database.CreateParameter("VelocityZ", prim.Velocity.Z));
            parameters.Add(_Database.CreateParameter("AngularVelocityX", prim.AngularVelocity.X));
            parameters.Add(_Database.CreateParameter("AngularVelocityY", prim.AngularVelocity.Y));
            parameters.Add(_Database.CreateParameter("AngularVelocityZ", prim.AngularVelocity.Z));
            parameters.Add(_Database.CreateParameter("AccelerationX", prim.Acceleration.X));
            parameters.Add(_Database.CreateParameter("AccelerationY", prim.Acceleration.Y));
            parameters.Add(_Database.CreateParameter("AccelerationZ", prim.Acceleration.Z));
            // quaternions
            parameters.Add(_Database.CreateParameter("RotationX", prim.RotationOffset.X));
            parameters.Add(_Database.CreateParameter("RotationY", prim.RotationOffset.Y));
            parameters.Add(_Database.CreateParameter("RotationZ", prim.RotationOffset.Z));
            parameters.Add(_Database.CreateParameter("RotationW", prim.RotationOffset.W));

            // Sit target
            Vector3 sitTargetPos = prim.SitTargetPositionLL;
            parameters.Add(_Database.CreateParameter("SitTargetOffsetX", sitTargetPos.X));
            parameters.Add(_Database.CreateParameter("SitTargetOffsetY", sitTargetPos.Y));
            parameters.Add(_Database.CreateParameter("SitTargetOffsetZ", sitTargetPos.Z));

            Quaternion sitTargetOrient = prim.SitTargetOrientationLL;
            parameters.Add(_Database.CreateParameter("SitTargetOrientW", sitTargetOrient.W));
            parameters.Add(_Database.CreateParameter("SitTargetOrientX", sitTargetOrient.X));
            parameters.Add(_Database.CreateParameter("SitTargetOrientY", sitTargetOrient.Y));
            parameters.Add(_Database.CreateParameter("SitTargetOrientZ", sitTargetOrient.Z));

            parameters.Add(_Database.CreateParameter("PayPrice", prim.PayPrice[0]));
            parameters.Add(_Database.CreateParameter("PayButton1", prim.PayPrice[1]));
            parameters.Add(_Database.CreateParameter("PayButton2", prim.PayPrice[2]));
            parameters.Add(_Database.CreateParameter("PayButton3", prim.PayPrice[3]));
            parameters.Add(_Database.CreateParameter("PayButton4", prim.PayPrice[4]));

            if ((prim.SoundFlags & 1) != 0) // Looped
            {
                parameters.Add(_Database.CreateParameter("LoopedSound", prim.Sound));
                parameters.Add(_Database.CreateParameter("LoopedSoundGain", prim.SoundGain));
            }
            else
            {
                parameters.Add(_Database.CreateParameter("LoopedSound", UUID.Zero));
                parameters.Add(_Database.CreateParameter("LoopedSoundGain", 0.0f));
            }

            parameters.Add(_Database.CreateParameter("TextureAnimation", prim.TextureAnimation));
            parameters.Add(_Database.CreateParameter("ParticleSystem", prim.ParticleSystem));

            parameters.Add(_Database.CreateParameter("OmegaX", prim.AngularVelocity.X));
            parameters.Add(_Database.CreateParameter("OmegaY", prim.AngularVelocity.Y));
            parameters.Add(_Database.CreateParameter("OmegaZ", prim.AngularVelocity.Z));

            parameters.Add(_Database.CreateParameter("CameraEyeOffsetX", prim.CameraEyeOffset.X));
            parameters.Add(_Database.CreateParameter("CameraEyeOffsetY", prim.CameraEyeOffset.Y));
            parameters.Add(_Database.CreateParameter("CameraEyeOffsetZ", prim.CameraEyeOffset.Z));

            parameters.Add(_Database.CreateParameter("CameraAtOffsetX", prim.CameraAtOffset.X));
            parameters.Add(_Database.CreateParameter("CameraAtOffsetY", prim.CameraAtOffset.Y));
            parameters.Add(_Database.CreateParameter("CameraAtOffsetZ", prim.CameraAtOffset.Z));

            if (prim.ForceMouselook)
                parameters.Add(_Database.CreateParameter("ForceMouselook", 1));
            else
                parameters.Add(_Database.CreateParameter("ForceMouselook", 0));

            parameters.Add(_Database.CreateParameter("ScriptAccessPin", prim.ScriptAccessPin));

            if (prim.AllowedDrop)
                parameters.Add(_Database.CreateParameter("AllowedDrop", 1));
            else
                parameters.Add(_Database.CreateParameter("AllowedDrop", 0));

            if (prim.DIE_AT_EDGE)
                parameters.Add(_Database.CreateParameter("DieAtEdge", 1));
            else
                parameters.Add(_Database.CreateParameter("DieAtEdge", 0));

            parameters.Add(_Database.CreateParameter("SalePrice", prim.SalePrice));
            parameters.Add(_Database.CreateParameter("SaleType", prim.ObjectSaleType));

            byte clickAction = prim.ClickAction;
            parameters.Add(_Database.CreateParameter("ClickAction", clickAction));

            parameters.Add(_Database.CreateParameter("Material", prim.Material));

            parameters.Add(_Database.CreateParameter("CollisionSound", prim.CollisionSound));
            parameters.Add(_Database.CreateParameter("CollisionSoundVolume", prim.CollisionSoundVolume));
            parameters.Add(_Database.CreateParameter("PassTouches", prim.PassTouch));
            parameters.Add(_Database.CreateParameter("LinkNumber", prim.LinkNum));
            parameters.Add(_Database.CreateParameter("MediaURL", prim.MediaUrl));
			parameters.Add(_Database.CreateParameter("Generic", prim.GenericData));
            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the primshape parameters for stroing in DB.
        /// </summary>
        /// <param name="prim">Basic data of SceneObjectpart prim.</param>
        /// <param name="sceneGroupID">The scene group ID.</param>
        /// <param name="regionUUID">The region UUID.</param>
        /// <returns></returns>
        private SqlParameter[] CreatePrimShapeParameters(SceneObjectPart prim, UUID sceneGroupID, UUID regionUUID)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            PrimitiveBaseShape s = prim.Shape;
            parameters.Add(_Database.CreateParameter("UUID", prim.UUID));
            // shape is an enum
            parameters.Add(_Database.CreateParameter("Shape", 0));
            // vectors
            parameters.Add(_Database.CreateParameter("ScaleX", s.Scale.X));
            parameters.Add(_Database.CreateParameter("ScaleY", s.Scale.Y));
            parameters.Add(_Database.CreateParameter("ScaleZ", s.Scale.Z));
            // paths
            parameters.Add(_Database.CreateParameter("PCode", s.PCode));
            parameters.Add(_Database.CreateParameter("PathBegin", s.PathBegin));
            parameters.Add(_Database.CreateParameter("PathEnd", s.PathEnd));
            parameters.Add(_Database.CreateParameter("PathScaleX", s.PathScaleX));
            parameters.Add(_Database.CreateParameter("PathScaleY", s.PathScaleY));
            parameters.Add(_Database.CreateParameter("PathShearX", s.PathShearX));
            parameters.Add(_Database.CreateParameter("PathShearY", s.PathShearY));
            parameters.Add(_Database.CreateParameter("PathSkew", s.PathSkew));
            parameters.Add(_Database.CreateParameter("PathCurve", s.PathCurve));
            parameters.Add(_Database.CreateParameter("PathRadiusOffset", s.PathRadiusOffset));
            parameters.Add(_Database.CreateParameter("PathRevolutions", s.PathRevolutions));
            parameters.Add(_Database.CreateParameter("PathTaperX", s.PathTaperX));
            parameters.Add(_Database.CreateParameter("PathTaperY", s.PathTaperY));
            parameters.Add(_Database.CreateParameter("PathTwist", s.PathTwist));
            parameters.Add(_Database.CreateParameter("PathTwistBegin", s.PathTwistBegin));
            // profile
            parameters.Add(_Database.CreateParameter("ProfileBegin", s.ProfileBegin));
            parameters.Add(_Database.CreateParameter("ProfileEnd", s.ProfileEnd));
            parameters.Add(_Database.CreateParameter("ProfileCurve", s.ProfileCurve));
            parameters.Add(_Database.CreateParameter("ProfileHollow", s.ProfileHollow));
            parameters.Add(_Database.CreateParameter("Texture", s.TextureEntry));
            parameters.Add(_Database.CreateParameter("ExtraParams", s.ExtraParams));
            parameters.Add(_Database.CreateParameter("State", s.State));
            parameters.Add(_Database.CreateParameter("Media", null == s.Media ? null : s.Media.ToXml()));

            return parameters.ToArray();
        }

        #endregion

        #endregion
    }
}