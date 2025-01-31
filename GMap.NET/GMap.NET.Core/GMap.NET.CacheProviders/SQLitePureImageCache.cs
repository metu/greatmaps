﻿
namespace GMap.NET.CacheProviders
{

   using System.Collections.Generic;
   using System.Data.Common;
   using System.IO;
   using System.Text;
   using System;
   using System.Diagnostics;
   using System.Globalization;
   using GMap.NET.MapProviders;
   using System.Threading;

   using Microsoft.Data.Sqlite;
   using GMap.NET.Internals;
   using GMap.NET.Core.GMap.NET;

   /// <summary>
   /// ultra fast cache system for tiles
   /// </summary>
   public class SQLitePureImageCache : PureImageCache
   {
      static SQLitePureImageCache()
      {

      }

      string cache;
      string gtileCache;
      string dir;
      string db;
      bool Created = false;

      public string GtileCache
      {
         get
         {
            return gtileCache;
         }
      }

      /// <summary>
      /// local cache location
      /// </summary>
      public string CacheLocation
      {
         get
         {
            return cache;
         }
         set
         {
            cache = value;

            gtileCache = Path.Combine(cache, "TileDBv5") + Path.DirectorySeparatorChar;

            dir = gtileCache + GMapProvider.LanguageStr + Path.DirectorySeparatorChar;

            // precreate dir
            if (!Directory.Exists(dir))
            {
               Directory.CreateDirectory(dir);
            }

            // make empty db
            {
               db = dir + "Data.gmdb";

               if (!File.Exists(db))
               {
                  Created = CreateEmptyDB(db);
               }
               else
               {
                  Created = AlterDBAddTimeColumn(db);
               }

               CheckPreAllocation();

               //var connBuilder = new SQLiteConnectionStringBuilder();
               //connBuilder.DataSource = "c:\filePath.db";
               //connBuilder.Version = 3;
               //connBuilder.PageSize = 4096;
               //connBuilder.JournalMode = SQLiteJournalModeEnum.Wal;
               //connBuilder.Pooling = true;
               //var x = connBuilder.ToString();
               ConnectionString = string.Format("Data Source=\"{0}\";Page Size=32768;Pooling=True", db); //;Journal Mode=Wal
            }

            // clear old attachments
            AttachedCaches.Clear();
            RebuildFinnalSelect();

            // attach all databases from main cache location
            var dbs = Directory.GetFiles(dir, "*.gmdb", SearchOption.AllDirectories);

            foreach (var d in dbs)
            {
               if (d != db)
               {
                  Attach(d);
               }
            }
         }
      }

      /// <summary>
      /// pre-allocate 32MB free space 'ahead' if needed,
      /// decreases fragmentation
      /// </summary>
      void CheckPreAllocation()
      {
         {
            byte[] pageSizeBytes = new byte[2];
            byte[] freePagesBytes = new byte[4];

            lock (this)
            {
               using (var dbf = File.Open(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
               {
                  dbf.Seek(16, SeekOrigin.Begin);

                  dbf.Lock(16, 2);
                  dbf.Read(pageSizeBytes, 0, 2);
                  dbf.Unlock(16, 2);

                  dbf.Seek(36, SeekOrigin.Begin);

                  dbf.Lock(36, 4);
                  dbf.Read(freePagesBytes, 0, 4);
                  dbf.Unlock(36, 4);

                  dbf.Close();
               }
            }

            if (BitConverter.IsLittleEndian)
            {
               Array.Reverse(pageSizeBytes);
               Array.Reverse(freePagesBytes);
            }
            UInt16 pageSize = BitConverter.ToUInt16(pageSizeBytes, 0);
            UInt32 freePages = BitConverter.ToUInt32(freePagesBytes, 0);

            var freeMB = (pageSize * freePages) / (1024.0 * 1024.0);

            int addSizeMB = 32;
            int waitUntilMB = 4;

            Debug.WriteLine("FreePageSpace in cache: " + freeMB + "MB | " + freePages + " pages");

            if (freeMB <= waitUntilMB)
            {
               PreAllocateDB(db, addSizeMB);
            }
         }
      }

      #region -- import / export --
      public static bool CreateEmptyDB(string file)
      {
         bool ret = true;

         try
         {
            string dir = Path.GetDirectoryName(file);
            if (!Directory.Exists(dir))
            {
               Directory.CreateDirectory(dir);
            }
            if (!File.Exists(file))
            {
               using (FileStream fileStream = File.Create(file))
               {
               }
            }

            using (SqliteConnection cn = new SqliteConnection())
            {
               cn.ConnectionString = string.Format("Data Source=\"{0}\";", file);

               SQLitePCL.Batteries.Init();

               cn.Open();
               {
                  using (DbTransaction tr = cn.BeginTransaction())
                  {
                     try
                     {
                        using (DbCommand cmd = cn.CreateCommand())
                        {
                           cmd.Transaction = tr;
                           cmd.CommandText = SqlCommandList.CreateTable;
                           cmd.ExecuteNonQuery();
                        }
                        tr.Commit();
                     }
                     catch (Exception exx)
                     {
                        Debug.WriteLine("CreateEmptyDB: " + exx.ToString());

                        tr.Rollback();
                        ret = false;
                     }
                  }
                  cn.Close();
               }
            }
         }
         catch (Exception ex)
         {
            Debug.WriteLine("CreateEmptyDB: " + ex.ToString());
            ret = false;
         }
         return ret;
      }

      public static bool PreAllocateDB(string file, int addSizeInMBytes)
      {
         bool ret = true;

         try
         {
            Debug.WriteLine("PreAllocateDB: " + file + ", +" + addSizeInMBytes + "MB");

            using (SqliteConnection cn = new SqliteConnection())
            {
               cn.ConnectionString = string.Format("Data Source=\"{0}\";", file);
               cn.Open();
               {
                  using (DbTransaction tr = cn.BeginTransaction())
                  {
                     try
                     {
                        using (DbCommand cmd = cn.CreateCommand())
                        {
                           cmd.Transaction = tr;
                           cmd.CommandText = string.Format("create table large (a); insert into large values (zeroblob({0})); drop table large;", addSizeInMBytes * 1024 * 1024);
                           cmd.ExecuteNonQuery();
                        }
                        tr.Commit();
                     }
                     catch (Exception exx)
                     {
                        Debug.WriteLine("PreAllocateDB: " + exx.ToString());

                        tr.Rollback();
                        ret = false;
                     }
                  }
                  cn.Close();
               }
            }
         }
         catch (Exception ex)
         {
            Debug.WriteLine("PreAllocateDB: " + ex.ToString());
            ret = false;
         }
         return ret;
      }

      private static bool AlterDBAddTimeColumn(string file)
      {
         bool ret = true;

         try
         {
            if (File.Exists(file))
            {
               using (SqliteConnection cn = new SqliteConnection())
               {
                  cn.ConnectionString = string.Format("Data Source=\"{0}\";", file);
                  cn.Open();
                  {
                     using (DbTransaction tr = cn.BeginTransaction())
                     {
                        bool? NoCacheTimeColumn = null;

                        try
                        {
                           using (DbCommand cmd = new SqliteCommand("SELECT CacheTime FROM Tiles", cn))
                           {
                              cmd.Transaction = tr;

                              using (DbDataReader rd = cmd.ExecuteReader())
                              {
                                 rd.Close();
                              }
                              NoCacheTimeColumn = false;
                           }
                        }
                        catch (Exception ex)
                        {
                           if (ex.Message.Contains("no such column: CacheTime"))
                           {
                              NoCacheTimeColumn = true;
                           }
                           else
                           {
                              throw ex;
                           }
                        }

                        try
                        {
                           if (NoCacheTimeColumn.HasValue && NoCacheTimeColumn.Value)
                           {
                              using (DbCommand cmd = cn.CreateCommand())
                              {
                                 cmd.Transaction = tr;

                                 cmd.CommandText = "ALTER TABLE Tiles ADD CacheTime DATETIME";

                                 cmd.ExecuteNonQuery();
                              }
                              tr.Commit();
                              NoCacheTimeColumn = false;
                           }
                        }
                        catch (Exception exx)
                        {
                           Debug.WriteLine("AlterDBAddTimeColumn: " + exx.ToString());

                           tr.Rollback();
                           ret = false;
                        }
                     }
                     cn.Close();
                  }
               }
            }
            else
            {
               ret = false;
            }
         }
         catch (Exception ex)
         {
            Debug.WriteLine("AlterDBAddTimeColumn: " + ex.ToString());
            ret = false;
         }
         return ret;
      }

      public static bool VacuumDb(string file)
      {
         bool ret = true;

         try
         {
            using (SqliteConnection cn = new SqliteConnection())
            {
               cn.ConnectionString = string.Format("Data Source=\"{0}\"", file);
               cn.Open();
               {
                  using (DbCommand cmd = cn.CreateCommand())
                  {
                     cmd.CommandText = "vacuum;";
                     cmd.ExecuteNonQuery();
                  }
                  cn.Close();
               }
            }
         }
         catch (Exception ex)
         {
            Debug.WriteLine("VacuumDb: " + ex.ToString());
            ret = false;
         }
         return ret;
      }

      public static bool ExportMapDataToDB(string sourceFile, string destFile)
      {
         bool ret = true;

         try
         {
            if (!File.Exists(destFile))
            {
               ret = CreateEmptyDB(destFile);
            }

            if (ret)
            {
               using (SqliteConnection cn1 = new SqliteConnection())
               {
                  cn1.ConnectionString = string.Format("Data Source=\"{0}\";Page Size=32768", sourceFile);

                  cn1.Open();
                  if (cn1.State == System.Data.ConnectionState.Open)
                  {
                     using (SqliteConnection cn2 = new SqliteConnection())
                     {
                        cn2.ConnectionString = string.Format("Data Source=\"{0}\";Page Size=32768", destFile);
                        cn2.Open();
                        if (cn2.State == System.Data.ConnectionState.Open)
                        {
                           using (SqliteCommand cmd = new SqliteCommand(string.Format("ATTACH DATABASE \"{0}\" AS Source", sourceFile), cn2))
                           {
                              cmd.ExecuteNonQuery();
                           }

                           using (SqliteTransaction tr = cn2.BeginTransaction())
                           {
                              try
                              {
                                 List<long> add = new List<long>();
                                 using (SqliteCommand cmd = new SqliteCommand("SELECT id, X, Y, Zoom, Type FROM Tiles;", cn1))
                                 {
                                    using (SqliteDataReader rd = cmd.ExecuteReader())
                                    {
                                       while (rd.Read())
                                       {
                                          long id = rd.GetInt64(0);
                                          using (SqliteCommand cmd2 = new SqliteCommand(string.Format("SELECT id FROM Tiles WHERE X={0} AND Y={1} AND Zoom={2} AND Type={3};", rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3), rd.GetInt32(4)), cn2))
                                          {
                                             using (SqliteDataReader rd2 = cmd2.ExecuteReader())
                                             {
                                                if (!rd2.Read())
                                                {
                                                   add.Add(id);
                                                }
                                             }
                                          }
                                       }
                                    }
                                 }

                                 foreach (long id in add)
                                 {
                                    using (SqliteCommand cmd = new SqliteCommand(string.Format("INSERT INTO Tiles(X, Y, Zoom, Type, CacheTime) SELECT X, Y, Zoom, Type, CacheTime FROM Source.Tiles WHERE id={0}; INSERT INTO TilesData(id, Tile) Values((SELECT last_insert_rowid()), (SELECT Tile FROM Source.TilesData WHERE id={0}));", id), cn2))
                                    {
                                       cmd.Transaction = tr;
                                       cmd.ExecuteNonQuery();
                                    }
                                 }
                                 add.Clear();

                                 tr.Commit();
                              }
                              catch (Exception exx)
                              {
                                 Debug.WriteLine("ExportMapDataToDB: " + exx.ToString());
                                 tr.Rollback();
                                 ret = false;
                              }
                           }

                           using (SqliteCommand cmd = new SqliteCommand("DETACH DATABASE Source;", cn2))
                           {
                              cmd.ExecuteNonQuery();
                           }
                        }
                     }
                  }
               }
            }
         }
         catch (Exception ex)
         {
            Debug.WriteLine("ExportMapDataToDB: " + ex.ToString());
            ret = false;
         }
         return ret;
      }
      #endregion

      static readonly string singleSqlSelect = "SELECT Tile FROM main.TilesData WHERE id = (SELECT id FROM main.Tiles WHERE X={0} AND Y={1} AND Zoom={2} AND Type={3})";
      static readonly string singleSqlInsert = "INSERT INTO main.Tiles(X, Y, Zoom, Type, CacheTime) VALUES(@p1, @p2, @p3, @p4, @p5)";
      static readonly string singleSqlInsertLast = "INSERT INTO main.TilesData(id, Tile) VALUES((SELECT last_insert_rowid()), @p1)";

      string ConnectionString;

      readonly List<string> AttachedCaches = new List<string>();
      string finnalSqlSelect = singleSqlSelect;
      string attachSqlQuery = string.Empty;
      string detachSqlQuery = string.Empty;

      void RebuildFinnalSelect()
      {
         finnalSqlSelect = null;
         finnalSqlSelect = singleSqlSelect;

         attachSqlQuery = null;
         attachSqlQuery = string.Empty;

         detachSqlQuery = null;
         detachSqlQuery = string.Empty;

         int i = 1;
         foreach (var c in AttachedCaches)
         {
            finnalSqlSelect += string.Format("\nUNION SELECT Tile FROM db{0}.TilesData WHERE id = (SELECT id FROM db{0}.Tiles WHERE X={{0}} AND Y={{1}} AND Zoom={{2}} AND Type={{3}})", i);
            attachSqlQuery += string.Format("\nATTACH '{0}' as db{1};", c, i);
            detachSqlQuery += string.Format("\nDETACH DATABASE db{0};", i);

            i++;
         }
      }

      public void Attach(string db)
      {
         if (!AttachedCaches.Contains(db))
         {
            AttachedCaches.Add(db);
            RebuildFinnalSelect();
         }
      }

      public void Detach(string db)
      {
         if (AttachedCaches.Contains(db))
         {
            AttachedCaches.Remove(db);
            RebuildFinnalSelect();
         }
      }

      #region PureImageCache Members

      int preAllocationPing = 0;

      bool PureImageCache.PutImageToCache(byte[] tile, int type, GPoint pos, int zoom)
      {
         bool ret = true;
         if (Created)
         {
            try
            {
               using (SqliteConnection cn = new SqliteConnection())
               {
                  cn.ConnectionString = ConnectionString;
                  cn.Open();
                  {
                     using (DbTransaction tr = cn.BeginTransaction())
                     {
                        try
                        {
                           using (DbCommand cmd = cn.CreateCommand())
                           {
                              cmd.Transaction = tr;
                              cmd.CommandText = singleSqlInsert;

                              cmd.Parameters.Add(new SqliteParameter("@p1", pos.X));
                              cmd.Parameters.Add(new SqliteParameter("@p2", pos.Y));
                              cmd.Parameters.Add(new SqliteParameter("@p3", zoom));
                              cmd.Parameters.Add(new SqliteParameter("@p4", type));
                              cmd.Parameters.Add(new SqliteParameter("@p5", DateTime.Now));

                              cmd.ExecuteNonQuery();
                           }

                           using (DbCommand cmd = cn.CreateCommand())
                           {
                              cmd.Transaction = tr;

                              cmd.CommandText = singleSqlInsertLast;
                              cmd.Parameters.Add(new SqliteParameter("@p1", tile));

                              cmd.ExecuteNonQuery();
                           }
                           tr.Commit();
                        }
                        catch (Exception ex)
                        {
                           Debug.WriteLine("PutImageToCache: " + ex.ToString());

                           tr.Rollback();
                           ret = false;
                        }
                     }
                  }
                  cn.Close();
               }

               if (Interlocked.Increment(ref preAllocationPing) % 22 == 0)
               {
                  CheckPreAllocation();
               }
            }
            catch (Exception ex)
            {
               Debug.WriteLine("PutImageToCache: " + ex.ToString());
               ret = false;
            }
         }
         return ret;
      }

      PureImage PureImageCache.GetImageFromCache(int type, GPoint pos, int zoom)
      {
         PureImage ret = null;
         try
         {
            using (SqliteConnection cn = new SqliteConnection())
            {
               cn.ConnectionString = ConnectionString;
               cn.Open();
               {
                  if (!string.IsNullOrEmpty(attachSqlQuery))
                  {
                     using (DbCommand com = cn.CreateCommand())
                     {
                        com.CommandText = attachSqlQuery;
                        int x = com.ExecuteNonQuery();
                        //Debug.WriteLine("Attach: " + x);                         
                     }
                  }

                  using (DbCommand com = cn.CreateCommand())
                  {
                     com.CommandText = string.Format(finnalSqlSelect, pos.X, pos.Y, zoom, type);

                     using (DbDataReader rd = com.ExecuteReader(System.Data.CommandBehavior.SequentialAccess))
                     {
                        if (rd.Read())
                        {
                           long length = rd.GetBytes(0, 0, null, 0, 0);
                           byte[] tile = new byte[length];
                           rd.GetBytes(0, 0, tile, 0, tile.Length);
                           {
                              if (GMapProvider.TileImageProxy != null)
                              {
                                 ret = GMapProvider.TileImageProxy.FromArray(tile);
                              }
                           }
                           tile = null;
                        }
                        rd.Close();
                     }
                  }

                  if (!string.IsNullOrEmpty(detachSqlQuery))
                  {
                     using (DbCommand com = cn.CreateCommand())
                     {
                        com.CommandText = detachSqlQuery;
                        int x = com.ExecuteNonQuery();
                        //Debug.WriteLine("Detach: " + x);
                     }
                  }
               }
               cn.Close();
            }
         }
         catch (Exception ex)
         {
            Debug.WriteLine("GetImageFromCache: " + ex.ToString());
            ret = null;
         }

         return ret;
      }

      int PureImageCache.DeleteOlderThan(DateTime date, int? type)
      {
         int affectedRows = 0;

         try
         {
            using (SqliteConnection cn = new SqliteConnection())
            {
               cn.ConnectionString = ConnectionString;
               cn.Open();
               {
                  using (DbCommand com = cn.CreateCommand())
                  {
                     com.CommandText = string.Format("DELETE FROM Tiles WHERE CacheTime is not NULL and CacheTime < datetime('{0}')", date.ToString("s"));
                     if (type.HasValue)
                     {
                        com.CommandText += " and Type = " + type;
                     }
                     affectedRows = com.ExecuteNonQuery();
                  }
               }
            }
         }
         catch (Exception ex)
         {
            Debug.WriteLine("DeleteOlderThan: " + ex);
         }

         return affectedRows;
      }

      #endregion
   }
}
