﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LibOrbisPkg.PFS
{
  public class PfsProperties
  {
    public FSDir root;
    public long FileTime;
    public uint BlockSize;
    public bool Encrypt;
    public bool Sign;
    public byte[] EKPFS;
    public byte[] Seed;

    public static PfsProperties MakeInnerPFSProps(GP4.Gp4Project proj, string projDir)
    {
      var root = new FSDir();
      BuildFSTree(root, proj, projDir);
      var timestamp = DateTime.Parse(proj.volume.TimeStamp).ToUniversalTime().Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
      return new PfsProperties()
      {
        root = root,
        BlockSize = 0x10000,
        Encrypt = false,
        Sign = false,
        FileTime = GetTimeStamp(proj),
      };
    }

    public static PfsProperties MakeOuterPFSProps(GP4.Gp4Project proj, PfsBuilder innerPFS, byte[] EKPFS, bool encrypt = true)
    {
      var root = new FSDir();
      root.Files.Add(new FSFile(innerPFS)
      {
        Parent = root,
      });
      return new PfsProperties()
      {
        root = root,
        BlockSize = 0x10000,
        Encrypt = encrypt,
        Sign = true,
        EKPFS = EKPFS,
        Seed = new byte[16],
        FileTime = GetTimeStamp(proj),
      };
    }

    private static long GetTimeStamp(GP4.Gp4Project proj)
    {
      // FIXME: This is incorrect when DST of current time and project time are different
      var timestamp = DateTime.Parse(proj.volume.TimeStamp).ToUniversalTime().Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
      return (long)timestamp;
    }
    


    /// <summary>
    /// Takes a directory and a root node, and recursively makes a filesystem tree.
    /// </summary>
    /// <param name="root">Root directory of the image</param>
    /// <param name="proj">GP4 Project</param>
    /// <param name="projDir">Directory of GP4 file</param>
    static void BuildFSTree(FSDir root, GP4.Gp4Project proj, string projDir)
    {
      void AddDirs(FSDir parent, List<GP4.Dir> imgDir)
      {
        foreach (var d in imgDir)
        {
          FSDir dir;
          parent.Dirs.Add(dir = new FSDir { name = d.TargetName, Parent = parent });
          AddDirs(dir, d.Children);
        }
      }
      FSDir FindDir(string name)
      {
        FSDir dir = root;
        var breadcrumbs = name.Split('/');
        foreach (var crumb in breadcrumbs)
        {
          dir = dir.Dirs.Where(d => d.name == crumb).First();
        }
        return dir;
      }

      AddDirs(root, proj.RootDir);

      foreach (var f in proj.files)
      {
        var lastSlash = f.TargetPath.LastIndexOf('/') + 1;
        if (f.TargetPath == "sce_sys/param.sfo")
        {
          continue;
        }
        var name = f.TargetPath.Substring(lastSlash);
        var source = Path.Combine(projDir, f.OrigPath);
        var parent = lastSlash == 0 ? root : FindDir(f.TargetPath.Substring(0, lastSlash - 1));
        parent.Files.Add(new FSFile(source)
        {
          Parent = parent,
          name = name,
        });
      }
    }
  }
}