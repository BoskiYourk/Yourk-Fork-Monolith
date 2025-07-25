// SPDX-FileCopyrightText: 2021 DrSmugleaf
// SPDX-FileCopyrightText: 2021 Paul Ritter
// SPDX-FileCopyrightText: 2021 Pieter-Jan Briers
// SPDX-FileCopyrightText: 2021 Vera Aguilera Puerto
// SPDX-FileCopyrightText: 2021 Visne
// SPDX-FileCopyrightText: 2022 Acruid
// SPDX-FileCopyrightText: 2022 Emisse
// SPDX-FileCopyrightText: 2022 Francesco
// SPDX-FileCopyrightText: 2022 Jack Fox
// SPDX-FileCopyrightText: 2022 Kevin Zheng
// SPDX-FileCopyrightText: 2022 metalgearsloth
// SPDX-FileCopyrightText: 2023 AJCM-git
// SPDX-FileCopyrightText: 2023 Julian Giebel
// SPDX-FileCopyrightText: 2023 Leon Friedrich
// SPDX-FileCopyrightText: 2023 keronshb
// SPDX-FileCopyrightText: 2024 Nemanja
// SPDX-FileCopyrightText: 2024 Tayrtahn
// SPDX-FileCopyrightText: 2025 Milon
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.DeviceLinking.Events;
using Content.Server.DeviceLinking.Systems;
using Content.Server.Materials;
using Content.Shared.Conveyor;
using Content.Shared.Destructible;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Physics.Controllers;
using Content.Shared.Power;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Systems;

namespace Content.Server.Physics.Controllers;

public sealed class ConveyorController : SharedConveyorController
{
    [Dependency] private readonly FixtureSystem _fixtures = default!;
    [Dependency] private readonly DeviceLinkSystem _signalSystem = default!;
    [Dependency] private readonly MaterialReclaimerSystem _materialReclaimer = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        UpdatesAfter.Add(typeof(MoverController));
        SubscribeLocalEvent<ConveyorComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<ConveyorComponent, ComponentShutdown>(OnConveyorShutdown);
        SubscribeLocalEvent<ConveyorComponent, BreakageEventArgs>(OnBreakage);

        SubscribeLocalEvent<ConveyorComponent, SignalReceivedEvent>(OnSignalReceived);
        SubscribeLocalEvent<ConveyorComponent, PowerChangedEvent>(OnPowerChanged);

        base.Initialize();
    }

    private void OnInit(EntityUid uid, ConveyorComponent component, ComponentInit args)
    {
        _signalSystem.EnsureSinkPorts(uid, component.ReversePort, component.ForwardPort, component.OffPort);

        if (PhysicsQuery.TryComp(uid, out var physics))
        {
            var shape = new PolygonShape();
            shape.SetAsBox(0.55f, 0.55f);

            _fixtures.TryCreateFixture(uid, shape, ConveyorFixture,
                collisionLayer: (int) (CollisionGroup.LowImpassable | CollisionGroup.MidImpassable |
                                       CollisionGroup.Impassable), hard: false, body: physics);

        }
    }

    private void OnConveyorShutdown(EntityUid uid, ConveyorComponent component, ComponentShutdown args)
    {
        if (MetaData(uid).EntityLifeStage >= EntityLifeStage.Terminating)
            return;

        if (!PhysicsQuery.TryComp(uid, out var physics))
            return;

        _fixtures.DestroyFixture(uid, ConveyorFixture, body: physics);
    }

    private void OnBreakage(Entity<ConveyorComponent> ent, ref BreakageEventArgs args)
    {
        SetState(ent, ConveyorState.Off, ent);
    }

    private void OnPowerChanged(EntityUid uid, ConveyorComponent component, ref PowerChangedEvent args)
    {
        component.Powered = args.Powered;
        UpdateAppearance(uid, component);
        Dirty(uid, component);
    }

    private void UpdateAppearance(EntityUid uid, ConveyorComponent component)
    {
        _appearance.SetData(uid, ConveyorVisuals.State, component.Powered ? component.State : ConveyorState.Off);
    }

    private void OnSignalReceived(EntityUid uid, ConveyorComponent component, ref SignalReceivedEvent args)
    {
        if (args.Port == component.OffPort)
            SetState(uid, ConveyorState.Off, component);

        else if (args.Port == component.ForwardPort)
        {
            SetState(uid, ConveyorState.Forward, component);
        }

        else if (args.Port == component.ReversePort)
        {
            SetState(uid, ConveyorState.Reverse, component);
        }
    }

    private void SetState(EntityUid uid, ConveyorState state, ConveyorComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!_materialReclaimer.SetReclaimerEnabled(uid, state != ConveyorState.Off))
            return;

        component.State = state;

        if (state != ConveyorState.Off)
        {
            WakeConveyed(uid);
        }

        UpdateAppearance(uid, component);
        Dirty(uid, component);
    }

    /// <summary>
    /// Awakens sleeping entities on the conveyor belt's tile when it's turned on.
    /// Need this as we might activate under CollisionWake entities and need to forcefully check them.
    /// </summary>
    protected override void AwakenConveyor(Entity<TransformComponent?> ent)
    {
        if (!XformQuery.Resolve(ent.Owner, ref ent.Comp))
            return;

        var xform = ent.Comp;

        var beltTileRef = xform.Coordinates.GetTileRef(EntityManager, MapManager);

        if (beltTileRef != null)
        {
            Intersecting.Clear();
            Lookup.GetLocalEntitiesIntersecting(beltTileRef.Value.GridUid, beltTileRef.Value.GridIndices, Intersecting, 0f, flags: LookupFlags.Dynamic | LookupFlags.Sundries | LookupFlags.Approximate);

            foreach (var entity in Intersecting)
            {
                if (!PhysicsQuery.TryGetComponent(entity, out var physics))
                    continue;

                if (physics.BodyType != BodyType.Static)
                    PhysicsSystem.WakeBody(entity, body: physics);
            }
        }
    }
}
