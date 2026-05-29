//------------------------------------------------------------
// Game Framework
// Copyright © 2013-2021 Jiang Yin. All rights reserved.
// Homepage: https://gameframework.cn/
// Feedback: mailto:ellan@gameframework.cn
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;

namespace GameFramework.Resource
{
    internal sealed partial class ResourceManager : GameFrameworkModule, IResourceManager
    {
        /// <summary>
        /// 资源初始化器。
        /// </summary>
        private sealed class ResourceIniter
        {
            private readonly ResourceManager m_ResourceManager;
            private readonly Dictionary<ResourceName, string> m_CachedFileSystemNames;
            private string m_CurrentVariant;

            public GameFrameworkAction ResourceInitComplete;

            /// <summary>
            /// 初始化资源初始化器的新实例。
            /// </summary>
            /// <param name="resourceManager">资源管理器。</param>
            public ResourceIniter(ResourceManager resourceManager)
            {
                m_ResourceManager = resourceManager;
                m_CachedFileSystemNames = new Dictionary<ResourceName, string>();
                m_CurrentVariant = null;

                ResourceInitComplete = null;
            }

            /// <summary>
            /// 关闭并清理资源初始化器。
            /// </summary>
            public void Shutdown()
            {
            }

            /// <summary>
            /// 初始化资源。
            /// </summary>
            public void InitResources(string currentVariant)
            {
                m_CurrentVariant = currentVariant;

                if (m_ResourceManager.m_ResourceHelper == null)
                {
                    throw new GameFrameworkException("Resource helper is invalid.");
                }

                if (string.IsNullOrEmpty(m_ResourceManager.m_ReadOnlyPath))
                {
                    throw new GameFrameworkException("Readonly path is invalid.");
                }

                m_ResourceManager.m_ResourceHelper.LoadBytes(Utility.Path.GetRemotePath(Path.Combine(m_ResourceManager.m_ReadOnlyPath, RemoteVersionListFileName)), new LoadBytesCallbacks(OnLoadPackageVersionListSuccess, OnLoadPackageVersionListFailure), null);
            }

            private void OnLoadPackageVersionListSuccess(string fileUri, byte[] bytes, float duration, object userData)
            {
                MemoryStream memoryStream = null;
                try
                {
                    memoryStream = new MemoryStream(bytes, false);

                    // 反序列化包版本列表。获取Asset,Resource, FileSystem和ResourceGroup等相关信息。
                    PackageVersionList versionList = m_ResourceManager.m_PackageVersionListSerializer.Deserialize(memoryStream);
                    if (!versionList.IsValid)
                    {
                        throw new GameFrameworkException("Deserialize package version list failure.");
                    }

                    PackageVersionList.Asset[] assets = versionList.GetAssets();
                    PackageVersionList.Resource[] resources = versionList.GetResources();
                    PackageVersionList.FileSystem[] fileSystems = versionList.GetFileSystems();
                    PackageVersionList.ResourceGroup[] resourceGroups = versionList.GetResourceGroups();
                    m_ResourceManager.m_ApplicableGameVersion = versionList.ApplicableGameVersion;
                    m_ResourceManager.m_InternalResourceVersion = versionList.InternalResourceVersion;
                    m_ResourceManager.m_AssetInfos = new Dictionary<string, AssetInfo>(assets.Length, StringComparer.Ordinal);
                    m_ResourceManager.m_ResourceInfos = new Dictionary<ResourceName, ResourceInfo>(resources.Length, new ResourceNameComparer());
                    //在ResourceManager中创建一个默认资源组。后续所有Resource都会被添加到这个默认资源组中。
                    ResourceGroup defaultResourceGroup = m_ResourceManager.GetOrAddResourceGroup(string.Empty);
                   
                    //确定每个Resource所在的fileSystem，以字典形式存储，key为ResourceName，value为FileSystemName。
                    //如果对应资源已存在变体，则跳过该资源。
                    foreach (PackageVersionList.FileSystem fileSystem in fileSystems)
                    {
                        int[] resourceIndexes = fileSystem.GetResourceIndexes();
                        foreach (int resourceIndex in resourceIndexes)
                        {
                            PackageVersionList.Resource resource = resources[resourceIndex];
                            if (resource.Variant != null && resource.Variant != m_CurrentVariant)
                            {
                                continue;
                            }

                            m_CachedFileSystemNames.Add(new ResourceName(resource.Name, resource.Variant, resource.Extension), fileSystem.Name);
                        }
                    }

                    //获取Resource中存储的Asset信息，最终以AssetInfo的形式存储在ResourceManager的字典中，key为AssetName，value为AssetInfo。
                    //AssetInfo包含Asset名称，所属Resource名称，依赖的Asset名称列表等信息。
                    //同时将Resource的信息以ResourceInfo的形式存储在ResourceManager的字典中，key为ResourceName，value为ResourceInfo。
                    //ResourceInfo包含Resource名称，所属FileSystem名称，加载方式，资源大小，资源哈希值等信息。
                    foreach (PackageVersionList.Resource resource in resources)
                    {
                        if (resource.Variant != null && resource.Variant != m_CurrentVariant)
                        {
                            continue;
                        }

                        ResourceName resourceName = new ResourceName(resource.Name, resource.Variant, resource.Extension);
                        int[] assetIndexes = resource.GetAssetIndexes();
                        
                        //这一段主要是为了获取当前Asset的依赖Assets，主要是获取这些依赖Assets的名称，以string的方式传递进AssetInfo中。
                        //每个Asset在Resource中都有一个独一无二的索引。
                        foreach (int assetIndex in assetIndexes)
                        {
                            PackageVersionList.Asset asset = assets[assetIndex];
                            int[] dependencyAssetIndexes = asset.GetDependencyAssetIndexes();
                            int index = 0;
                            string[] dependencyAssetNames = new string[dependencyAssetIndexes.Length];
                            foreach (int dependencyAssetIndex in dependencyAssetIndexes)
                            {
                                dependencyAssetNames[index++] = assets[dependencyAssetIndex].Name;
                            }

                            m_ResourceManager.m_AssetInfos.Add(asset.Name, new AssetInfo(asset.Name, resourceName, dependencyAssetNames));
                        }
                        
                        //获取Resource所在的FileSystem名称，如果没有找到对应的FileSystem，则默认为null。
                        string fileSystemName = null;
                        if (!m_CachedFileSystemNames.TryGetValue(resourceName, out fileSystemName))
                        {
                            fileSystemName = null;
                        }

                        m_ResourceManager.m_ResourceInfos.Add(resourceName, new ResourceInfo(resourceName, fileSystemName, (LoadType)resource.LoadType, resource.Length, resource.HashCode, true, true));
                        //将所有Resource添加到默认资源组中。
                        defaultResourceGroup.AddResource(resourceName, resource.Length, resource.Length);
                    }

                    // PackageVersionList中的ResourceGroup是结构体，仅作为反序列化的临时载体（immutable DTO），
                    // 循环结束后即被丢弃。而ResourceManager内部的ResourceGroup是类实例（mutable），
                    // 需要持久化存在于整个运行时，支持AddResource、追踪ReadyCount等状态变化。
                    // 因此这里将每个结构体ResourceGroup的数据提取出来，转换为类实例并注册到ResourceManager中。
                    // 每个ResourceGroup通过资源索引列表关联其包含的所有Resource。
                    foreach (PackageVersionList.ResourceGroup resourceGroup in resourceGroups)
                    {
                        ResourceGroup group = m_ResourceManager.GetOrAddResourceGroup(resourceGroup.Name);
                        int[] resourceIndexes = resourceGroup.GetResourceIndexes();
                        foreach (int resourceIndex in resourceIndexes)
                        {
                            PackageVersionList.Resource resource = resources[resourceIndex];
                            if (resource.Variant != null && resource.Variant != m_CurrentVariant)
                            {
                                continue;
                            }

                            group.AddResource(new ResourceName(resource.Name, resource.Variant, resource.Extension), resource.Length, resource.Length);
                        }
                    }

                    ResourceInitComplete();
                }
                catch (Exception exception)
                {
                    if (exception is GameFrameworkException)
                    {
                        throw;
                    }

                    throw new GameFrameworkException(Utility.Text.Format("Parse package version list exception '{0}'.", exception.ToString()), exception);
                }
                finally
                {
                    m_CachedFileSystemNames.Clear();
                    if (memoryStream != null)
                    {
                        memoryStream.Dispose();
                        memoryStream = null;
                    }
                }
            }

            private void OnLoadPackageVersionListFailure(string fileUri, string errorMessage, object userData)
            {
                throw new GameFrameworkException(Utility.Text.Format("Package version list '{0}' is invalid, error message is '{1}'.", fileUri, string.IsNullOrEmpty(errorMessage) ? "<Empty>" : errorMessage));
            }
        }
    }
}
